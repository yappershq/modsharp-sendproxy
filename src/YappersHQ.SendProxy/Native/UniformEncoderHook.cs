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
using Sharp.Shared.GameEntities;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Units;
using YappersHQ.SendProxy.Shared;

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
    // Hooked encoder fn -> (trampoline, FieldType). Read-only after Install.
    private static readonly Dictionary<nint, (nint Trampoline, FieldType Type)> _byFn = new();
    private static readonly List<IDetourHook> _hooks = new();

    private static volatile bool   _installed;
    private static ILogger?        _logger;
    private static IEntityManager? _entityManager;

    private const int MaxSubstituteBytes = 4096;

    // Install one detour per resolved encoder fn, all routed to SharedHook. Idempotent.
    public static bool Install(InterfaceBridge bridge, ILogger logger, IReadOnlyDictionary<nint, FieldType> encoderTypes)
    {
        if (_installed)
        {
            return true;
        }

        _logger        = logger;
        _entityManager = bridge.EntityManager;

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
            // Proxy dispatch (IProxyManager model): fire the registered proxy ONCE here in the shared pack,
            // with the entity context captured by EncodeCapture. SetAll → substitute into the shared snapshot
            // (seen by every client, enters the delta naturally). SetFor (per-recipient) is applied at the
            // per-client copy stage (consumed from the buffer). This is the O(1), thread-safe path.
            // Fast-path gate: EncodeCapture resolved (once per entity) whether ANY proxy targets this
            // entity's serializer. For the overwhelming majority of entities it does not, so skip the
            // per-field name-read + lookup entirely — this branch only runs for proxied serializers.
            if (EncodeCapture.SerializerHasProxy && _entityManager is { } em)
            {
                var entIdx = EncodeCapture.EntityIndex;
                if (entIdx >= 0)
                {
                    // Match by the engine's stable name POINTERS (serializer + field) via byte-compare — no
                    // managed string built on this per-field path. fieldInfo+0x08 = char* m_pszFieldName.
                    var serNamePtr   = EncodeCapture.SerNamePtr;
                    var fieldNamePtr = NativeUtil.IsUserPtr(fieldInfo) ? *(nint*) (fieldInfo + 0x08) : 0;

                    ProxyRegistry.Entry proxy = default;
                    var matched = fieldNamePtr != 0
                                  && ProxyRegistry.TryGetByPtr(serNamePtr, fieldNamePtr, entIdx, out proxy);

                    if (matched && proxy.Callback is not null)
                    {
                        var entity = em.FindEntityByIndex((EntityIndex) entIdx);
                        if (entity is not null)
                        {
                            // ProxyField.Name reuses the registration's own string — no new string here.
                            var ctx = new ProxyContext(entity, new ProxyField(proxy.Field!, MapKind(entry.Type)),
                                ReadOriginal(entry.Type, valuePtr), ProxyRegistry.RentPerClientBuffer());

                            var invoked = false;
                            try
                            {
                                proxy.Callback(ref ctx);
                                invoked = true;
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "SendProxy: proxy threw for \"{Ser}::{Field}\" — passthrough", proxy.Serializer, proxy.Field);
                            }

                            // SetFor (per-recipient): record the overrides for the per-client copy stage to
                            // consume, and — if there's no uniform value — write one recipient's value into
                            // the SHARED snapshot so the field differs from baseline and enters EVERY client's
                            // delta. The per-client copy (FieldSubstitution) then applies each recipient's
                            // value or restores the real value for non-recipients.
                            if (invoked && ctx.HasPerClient)
                            {
                                var def = ctx.HasUniform ? ctx.UniformValue : ctx.Original;
                                PerClientDispatch.Record(entIdx, fieldNamePtr, entry.Type, in def, ctx.PerClientValues);

                                if (!ctx.HasUniform && ctx.PerClientValues.Count > 0)
                                {
                                    var force = ctx.PerClientValues[0].Value;
                                    var fr    = EncodeWith(in force, entry.Type, tramp, bf, fieldInfo, paramsPtr, valuePtr, extra, out var fok);
                                    if (fok)
                                    {
                                        return fr;
                                    }
                                }
                            }

                            if (invoked && ctx.HasUniform)
                            {
                                var r = EncodeWith(in ctx.UniformValue, entry.Type, tramp, bf, fieldInfo, paramsPtr, valuePtr, extra, out var ok);
                                if (ok)
                                {
                                    return r;
                                }
                            }
                        }
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

    // Read the field's REAL value (the value the encoder is about to write) into a SpoofValue, so the proxy
    // callback can see Original and so the per-viewer path can restore it for non-recipients. valuePtr is the
    // value pointer the encoder reads; its shape matches the encoder family. Guarded — returns default on any
    // bad pointer (the callback then just gets an empty Original, which is safe).
    private static SpoofValue ReadOriginal(FieldType type, nint valuePtr)
    {
        if (!NativeUtil.IsUserPtr(valuePtr))
        {
            return default;
        }

        switch (type)
        {
            case FieldType.Int32:
            case FieldType.Fixed32:
                return SpoofValue.Int(*(int*) valuePtr);
            case FieldType.UInt32:
                return SpoofValue.Int(unchecked((int) *(uint*) valuePtr));
            case FieldType.Int64:
            case FieldType.Fixed64:
                return SpoofValue.Int(unchecked((int) *(long*) valuePtr));
            case FieldType.Bool:
                return SpoofValue.Bool(*(byte*) valuePtr != 0);
            case FieldType.Float32:
            case FieldType.QuantizedFloat:
            case FieldType.Coord3:
            case FieldType.Normal3:
            case FieldType.CoordIntegral3:
                return SpoofValue.Float(*(float*) valuePtr);
            case FieldType.QAngle3:
            case FieldType.Vector3:
                return SpoofValue.Vector(new System.Numerics.Vector3(
                    ((float*) valuePtr)[0], ((float*) valuePtr)[1], ((float*) valuePtr)[2]));
            // String / ByteArray: Original is left empty. Materializing a String field's live value here
            // would allocate a managed string on the encode path; proxies that need the original string can
            // read it from the entity via schema. (The substitute value the callback SETS is unaffected.)
            default:
                return default;
        }
    }

    // Map a resolved encoder FieldType to the SpoofValue family a proxy callback uses (for ProxyField.Kind).
    private static SpoofKind MapKind(FieldType t)
        => t switch
        {
            FieldType.Float32 or FieldType.Coord3 or FieldType.Normal3
                or FieldType.CoordIntegral3 or FieldType.QuantizedFloat => SpoofKind.Float,
            FieldType.QAngle3 or FieldType.Vector3 => SpoofKind.Vector,
            FieldType.Bool      => SpoofKind.Bool,
            FieldType.String    => SpoofKind.String,
            FieldType.ByteArray => SpoofKind.Bytes,
            _                   => SpoofKind.Int,
        };

    // Encode a SpoofValue into the field via the engine encoder, building the scratch in the layout the
    // field's actual encoder (type) reads. The stackalloc + Invoke both happen in THIS frame so string/byte
    // buffers stay live during the encode. ok=false → the value kind is incompatible or oversized for this
    // field; the caller then passes the real value through.
    private static nint EncodeWith(in SpoofValue v, FieldType type, nint tramp, nint bf, nint fieldInfo,
        nint paramsPtr, nint valuePtr, uint extra, out bool ok)
    {
        ok = true;
        var scratch    = stackalloc byte[0x30];
        var stringSlot = stackalloc nint[1];

        switch (v.Kind)
        {
            case SpoofKind.Int:
                switch (type)
                {
                    case FieldType.UInt32:
                        *(ulong*) scratch = (uint) v.RawIntBits;

                        return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                    case FieldType.Int32:
                    case FieldType.Int64:
                    case FieldType.Fixed32:
                    case FieldType.Fixed64:
                        *(long*) scratch = v.RawIntBits;

                        return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                    case FieldType.Bool:
                        *scratch = (byte) (v.RawIntBits != 0 ? 1 : 0);

                        return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                    default:
                        ok = false;

                        return 0;
                }

            case SpoofKind.Bool:
                if (type != FieldType.Bool)
                {
                    ok = false;

                    return 0;
                }

                *scratch = (byte) (v.RawIntBits != 0 ? 1 : 0);

                return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);

            case SpoofKind.Float:
                if (type == FieldType.Float32)
                {
                    *(double*) scratch = v.RawFloat;

                    return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                }

                if (IsStructFloatType(type))
                {
                    CopyRealOrZero(scratch, valuePtr);
                    ((float*) scratch)[0] = v.RawFloat;

                    return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
                }

                ok = false;

                return 0;

            case SpoofKind.Vector:
                if (!IsVectorScratchType(type))
                {
                    ok = false;

                    return 0;
                }

                CopyRealOrZero(scratch, valuePtr);
                ((float*) scratch)[0] = v.RawVec.X;
                ((float*) scratch)[1] = v.RawVec.Y;
                ((float*) scratch)[2] = v.RawVec.Z;

                return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);

            case SpoofKind.String:
            {
                if (type != FieldType.String)
                {
                    ok = false;

                    return 0;
                }

                var s       = v.RawStr ?? string.Empty;
                var byteLen = Encoding.UTF8.GetByteCount(s);
                if (byteLen > MaxSubstituteBytes)
                {
                    ok = false;

                    return 0;
                }

                var strBuf = stackalloc byte[byteLen + 1];
                Encoding.UTF8.GetBytes(s, new Span<byte>(strBuf, byteLen));
                strBuf[byteLen] = 0;
                *stringSlot     = (nint) strBuf;

                return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) stringSlot, extra);
            }

            case SpoofKind.Bytes:
            {
                if (type != FieldType.ByteArray)
                {
                    ok = false;

                    return 0;
                }

                var b = v.RawBytes ?? Array.Empty<byte>();
                if (b.Length > MaxSubstituteBytes)
                {
                    ok = false;

                    return 0;
                }

                var buf = stackalloc byte[b.Length == 0 ? 1 : b.Length];
                for (var i = 0; i < b.Length; i++)
                {
                    buf[i] = b[i];
                }

                for (var i = 0; i < 0x30; i++)
                {
                    scratch[i] = 0;
                }

                *(nint*) (scratch + 0x00) = (nint) buf;
                *(uint*) (scratch + 0x28) = (uint) b.Length;

                return Invoke(tramp, bf, fieldInfo, paramsPtr, (nint) scratch, extra);
            }

            default:
                ok = false;

                return 0;
        }
    }

    // Copy the real value struct (preserving the component-count/mode at +0x28 that coord/quant encoders
    // read) into scratch, or zero it if the real pointer is unreadable.
    private static void CopyRealOrZero(byte* scratch, nint valuePtr)
    {
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
    }
}
