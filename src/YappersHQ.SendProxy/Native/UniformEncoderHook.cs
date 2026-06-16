/*
 * SendProxy for ModSharp (CS2)
 * Copyright (C) 2026 YappersHQ. All Rights Reserved.
 *
 * This file is part of SendProxy for ModSharp.
 * SendProxy is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 *
 * SendProxy is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with SendProxy. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

// Uniform (all-clients) value substitution by hooking the per-field encoder directly. Unlike the
// per-client bit-copy path (FieldSubstitution), this does not have to pair a value-copy to a field: the
// engine calls the encoder with the field's CNetworkSerializerFieldInfo as an argument, so the field
// identity is unambiguous. The encoder runs once during the shared snapshot pack (PackEntities), so a
// substitution here is seen by every client. ABI (System V):
//   enc(rdi = bf_write*, rsi = fieldInfo*, rdx = paramsPtr, rcx = valuePtr, r8d = extra) -> value
// The encoder reads the value through rcx, so substitution = build a scratch holding the fake value in
// the layout this encoder expects and call the original (trampoline) with rcx pointed at the scratch.
//
// One shared managed hook serves every hooked encoder: it derives which encoder fired from the live
// dispatch fn (*(*(fieldInfo+0x38))) — the same address that was hooked — and looks up that fn's
// trampoline + bucket FieldType. Native reads are IsUserPtr-gated; the body never throws out (the one
// catch is the unmanaged-callback boundary).
internal static unsafe class UniformEncoderHook
{
    // What the consumer asked to write. The hook checks this against the field's actual encoder type
    // (so an int value isn't applied to a float field) and builds the scratch in that encoder's layout.
    private enum ValueKind { Int, Float, Bool, Vector, String, Bytes }

    private readonly struct Spoof
    {
        public readonly ValueKind Kind;
        public readonly int       IntBits;
        public readonly Vector3   Vec;
        public readonly string?   Str;
        public readonly byte[]?   Bytes;

        public Spoof(ValueKind kind, int intBits, Vector3 vec, string? str, byte[]? bytes)
        {
            Kind    = kind;
            IntBits = intBits;
            Vec     = vec;
            Str     = str;
            Bytes   = bytes;
        }
    }

    // Field name -> spoof. Uniform spoofs apply to every entity carrying a field of that name.
    private static readonly ConcurrentDictionary<string, Spoof> _byName = new();

    // Hooked encoder fn -> (trampoline, FieldType). Read-only after Install.
    private static readonly Dictionary<nint, (nint Trampoline, FieldType Type)> _byFn = new();
    private static readonly List<IDetourHook> _hooks = new();

    private static volatile bool _installed;
    private static ILogger?      _logger;

    private const int MaxSubstituteBytes = 4096;

    public static bool HasAny => !_byName.IsEmpty;

    public static void SetInt(string field, int value)
        => _byName[field] = new Spoof(ValueKind.Int, value, default, null, null);

    public static void SetFloat(string field, float value)
        => _byName[field] = new Spoof(ValueKind.Float, BitConverter.SingleToInt32Bits(value), default, null, null);

    public static void SetBool(string field, bool value)
        => _byName[field] = new Spoof(ValueKind.Bool, value ? 1 : 0, default, null, null);

    public static void SetVector(string field, Vector3 value)
        => _byName[field] = new Spoof(ValueKind.Vector, 0, value, null, null);

    public static void SetString(string field, string value)
        => _byName[field] = new Spoof(ValueKind.String, 0, default, value, null);

    public static void SetBytes(string field, byte[] value)
        => _byName[field] = new Spoof(ValueKind.Bytes, 0, default, null, value);

    public static void Remove(string field)
        => _byName.TryRemove(field, out _);

    public static void Clear()
        => _byName.Clear();

    // Install one detour per resolved encoder fn, all routed to SharedHook. Idempotent.
    public static bool Install(InterfaceBridge bridge, ILogger logger, IReadOnlyDictionary<nint, FieldType> encoderTypes)
    {
        if (_installed)
        {
            return true;
        }

        _logger = logger;

        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, nint>) &SharedHook;

        foreach (var (fn, type) in encoderTypes)
        {
            if (!NativeUtil.IsUserPtr(fn) || _byFn.ContainsKey(fn))
            {
                continue;
            }

            var hook = bridge.HookManager.CreateDetourHook();
            hook.Prepare(fn, hookFn);
            if (!hook.Install())
            {
                logger.LogWarning("UniformEncoderHook: failed to hook encoder fn=0x{Fn:X} ({Type})", fn, type);
                continue;
            }

            _byFn[fn] = (hook.Trampoline, type);
            _hooks.Add(hook);
        }

        _installed = _byFn.Count > 0;
        logger.LogInformation("UniformEncoderHook: installed {Count} encoder detours", _byFn.Count);

        return _installed;
    }

    public static void Uninstall()
    {
        foreach (var hook in _hooks)
        {
            hook.Uninstall();
            hook.Dispose();
        }

        _hooks.Clear();
        _byFn.Clear();
        _byName.Clear();
        _installed = false;
        _logger    = null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint SharedHook(nint bf, nint fieldInfo, nint paramsPtr, nint valuePtr, uint extra)
    {
        // Identify which encoder fired (= the hooked fn at the dispatch slot) to get its trampoline.
        nint encFn = 0;
        if (NativeUtil.IsUserPtr(fieldInfo))
        {
            var dispatch = *(nint*) (fieldInfo + 0x38);
            if (NativeUtil.IsUserPtr(dispatch))
            {
                encFn = *(nint*) dispatch;
            }
        }

        if (encFn == 0 || !_byFn.TryGetValue(encFn, out var entry))
        {
            // Should not happen (we only hook fns we recorded); without a trampoline there is nothing
            // safe to call, so return 0 rather than re-enter the hooked code.
            return 0;
        }

        var tramp = entry.Trampoline;

        try
        {
            if (!_byName.IsEmpty)
            {
                var name = NativeUtil.ReadFieldName(fieldInfo);
                if (name.Length > 0 && _byName.TryGetValue(name, out var sp))
                {
                    var scratch    = stackalloc byte[0x30];
                    var stringSlot = stackalloc nint[1];

                    // String and byte-array payloads must be allocated HERE, in SharedHook's own frame:
                    // the engine encoder dereferences the value pointer during Invoke, so a buffer
                    // stackalloc'd in a callee (TryBuildScratch) would already be reclaimed and the
                    // encoder would walk freed stack → garbage on the wire. Scalars are written straight
                    // into `scratch`, so they have no such lifetime issue and stay in TryBuildScratch.
                    if (sp.Kind == ValueKind.String && entry.Type == FieldType.String)
                    {
                        var s       = sp.Str ?? string.Empty;
                        var byteLen = Encoding.UTF8.GetByteCount(s);
                        if (byteLen <= MaxSubstituteBytes)
                        {
                            var strBuf = stackalloc byte[byteLen + 1];
                            Encoding.UTF8.GetBytes(s, new Span<byte>(strBuf, byteLen));
                            strBuf[byteLen] = 0;
                            *stringSlot    = (nint) strBuf;   // encoder reads *valuePtr as char*
                            return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) stringSlot, extra);
                        }
                    }
                    else if (sp.Kind == ValueKind.Bytes && entry.Type == FieldType.ByteArray)
                    {
                        var b = sp.Bytes ?? Array.Empty<byte>();
                        if (b.Length <= MaxSubstituteBytes)
                        {
                            var buf = stackalloc byte[b.Length == 0 ? 1 : b.Length];
                            for (var i = 0; i < b.Length; i++)
                            {
                                buf[i] = b[i];
                            }

                            for (var i = 0; i < 0x30; i++)
                            {
                                scratch[i] = 0;
                            }

                            *(nint*) (scratch + 0x00) = (nint) buf;     // {data*, +0x28 count}
                            *(uint*) (scratch + 0x28) = (uint) b.Length;
                            return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                        }
                    }
                    else if (sp.Kind == ValueKind.Vector && IsVectorScratchType(entry.Type))
                    {
                        // The vector family splits in two: qangle/normal/vector3 read 3 bare floats at
                        // valuePtr+0; coord/coord_integral/quantized read a value STRUCT with a
                        // component-count/mode at +0x28 and extra float lanes. Writing only 3 floats into a
                        // bare scratch leaves +0x28 garbage → the struct encoders loop a garbage count and
                        // corrupt the stream. So copy the REAL value struct (the original valuePtr, which
                        // carries the correct +0x28 and layout) and patch the leading x/y/z. Correct for the
                        // 3-float encoders (they read exactly the patched lanes) and safe for the struct
                        // encoders (count/extra lanes preserved). Falls back to a zeroed scratch if the real
                        // pointer is unreadable.
                        if (NativeUtil.IsUserPtr(valuePtr))
                        {
                            for (var i = 0; i < 0x30; i++)
                            {
                                scratch[i] = *(byte*) (valuePtr + i);
                            }
                        }
                        else
                        {
                            for (var i = 0; i < 0x30; i++)
                            {
                                scratch[i] = 0;
                            }
                        }

                        ((float*) scratch)[0] = sp.Vec.X;
                        ((float*) scratch)[1] = sp.Vec.Y;
                        ((float*) scratch)[2] = sp.Vec.Z;
                        return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                    }
                    else if (sp.Kind == ValueKind.Float && IsStructFloatType(entry.Type))
                    {
                        // A float spoof on a struct-float encoder (single-component quantized float such as
                        // m_flViewmodelFOV, or coord/normal). These read a value STRUCT (floats + a
                        // count/mode at +0x28), NOT a bare double like the b4 float32 encoder — so copy the
                        // real struct to preserve +0x28 and patch the leading component with the fake float.
                        if (NativeUtil.IsUserPtr(valuePtr))
                        {
                            for (var i = 0; i < 0x30; i++)
                            {
                                scratch[i] = *(byte*) (valuePtr + i);
                            }
                        }
                        else
                        {
                            for (var i = 0; i < 0x30; i++)
                            {
                                scratch[i] = 0;
                            }
                        }

                        ((float*) scratch)[0] = BitConverter.Int32BitsToSingle(sp.IntBits);
                        return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                    }
                    else if (TryBuildScratch(in sp, entry.Type, scratch, stringSlot, out var fakeValuePtr))
                    {
                        return Invoke(tramp, bf, fieldInfo, paramsPtr, fakeValuePtr, extra);
                    }
                }
            }
        }
        catch
        {
            // Boundary guard — fall through to the unmodified call.
        }

        return Invoke(tramp, bf, fieldInfo, paramsPtr, valuePtr, extra);
    }

    // Build the fake value in the layout the field's actual encoder (targetType) reads, but only if the
    // consumer's value kind is compatible with that encoder — otherwise pass through untouched (e.g. an
    // int value registered against a float field, or a vector against a string). Returns false to skip.
    private static bool TryBuildScratch(in Spoof sp, FieldType targetType, byte* scratch, nint* stringSlot, out nint valuePtr)
    {
        valuePtr = (nint) scratch;

        switch (sp.Kind)
        {
            case ValueKind.Int:
                switch (targetType)
                {
                    case FieldType.UInt32:
                        *(ulong*) scratch = (uint) sp.IntBits;

                        return true;
                    case FieldType.Int32:
                    case FieldType.Int64:
                    case FieldType.Fixed32:
                    case FieldType.Fixed64:
                        *(long*) scratch = sp.IntBits;

                        return true;
                    default:
                        return false;
                }

            case ValueKind.Float:
                if (targetType != FieldType.Float32)
                {
                    return false;
                }

                *(double*) scratch = BitConverter.Int32BitsToSingle(sp.IntBits);

                return true;

            case ValueKind.Bool:
                if (targetType != FieldType.Bool)
                {
                    return false;
                }

                *scratch = (byte) (sp.IntBits != 0 ? 1 : 0);

                return true;

            // Vector family is handled in SharedHook (it needs the original value struct to preserve the
            // component-count/mode at +0x28 that coord/coord_integral/quantized read).
            case ValueKind.Vector:
                return false;

            // String / ByteArray are handled in SharedHook (their payload buffer must live in the calling
            // frame because the engine encoder dereferences it after this method would have returned).
            case ValueKind.String:
            case ValueKind.Bytes:
            default:
                return false;
        }
    }

    // The encoder families a Vector spoof can drive — all read their components from valuePtr as floats
    // (the struct ones also carry a count/mode at +0x28, preserved by copying the real value struct).
    private static bool IsVectorScratchType(FieldType t)
        => t is FieldType.QAngle3 or FieldType.Vector3 or FieldType.Coord3
            or FieldType.Normal3 or FieldType.CoordIntegral3 or FieldType.QuantizedFloat;

    // Float-family encoders that read a value STRUCT (quantized/coord/normal) rather than a bare double
    // (b4 float32). A single-float spoof patches component[0] of the copied real struct.
    private static bool IsStructFloatType(FieldType t)
        => t is FieldType.QuantizedFloat or FieldType.Coord3
            or FieldType.Normal3 or FieldType.CoordIntegral3;

    private static nint Invoke(nint tramp, nint bf, nint fieldInfo, nint paramsPtr, nint valuePtr, uint extra)
        => ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, nint>) tramp)(bf, fieldInfo, paramsPtr, valuePtr, extra);
}
