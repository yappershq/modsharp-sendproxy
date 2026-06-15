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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Native;

// Per-client per-field value substitution. WriteFieldList runs once per changed field per client on
// an engine send thread; it calls GetBitRange (resolves the CFieldPath of the field being copied) then
// BitCopyPrimitive (copies the field's pre-encoded bits). Substitution rides BitCopyPrimitive: save the
// dst bit cursor, call the original, rewind, then re-emit the fake value through the field's own engine
// encoder. RE layout/offset details: docs/REVERSE_ENGINEERING.md §11/§15. All mutable per-call state is
// [ThreadStatic]; the registry and encoder maps are concurrent / read-only after Install.

internal enum SubstitutionMode
{
    Off,
    Verify,
    Fake,
}

// Network encoder family for a registered field. The Coord3/Normal3/CoordIntegral3/QuantizedFloat set
// requires reading the live value struct from the entity snapshot (the encoder's internal quant logic
// must see the real float); the others are encoded from a synthesized scratch. Unsupported MUST pass
// through untouched — writing wrong-type bits would corrupt the delta for every client.
internal enum FieldType
{
    Unsupported = 0,
    Int32, UInt32, Int64, Bool, Float32, Fixed32, Fixed64,
    QAngle3, Vector3,
    Coord3, Normal3, CoordIntegral3, QuantizedFloat,
}

internal enum CallbackKind
{
    None = 0,
    Int,
    Float,
    Bool,
    Vector,
}

internal static unsafe class FieldSubstitution
{
    private static volatile int _mode = (int) SubstitutionMode.Off;

    public static SubstitutionMode Mode
    {
        get => (SubstitutionMode) _mode;
        set => Interlocked.Exchange(ref _mode, (int) value);
    }

    // A registration holds an optional uniform spoof value and/or a per-client typed callback.
    // Key entityIndex: -1 = all entities (global), >= 0 = specific entity. ValueCopyHook probes the
    // entity-specific entry first, then the global fallback — both lock-free dictionary reads.
    private readonly struct SpoofEntry
    {
        public readonly bool                  HasSpoof;
        public readonly int                   SpoofValue;
        public readonly CallbackKind          CallbackType;
        public readonly PerClientIntProxy?    IntCallback;
        public readonly PerClientFloatProxy?  FloatCallback;
        public readonly PerClientBoolProxy?   BoolCallback;
        public readonly PerClientVectorProxy? VectorCallback;

        public SpoofEntry(int spoofValue) : this()
        {
            HasSpoof   = true;
            SpoofValue = spoofValue;
        }

        public SpoofEntry(PerClientIntProxy callback) : this()
        {
            CallbackType = CallbackKind.Int;
            IntCallback  = callback;
        }

        public SpoofEntry(PerClientFloatProxy callback) : this()
        {
            CallbackType  = CallbackKind.Float;
            FloatCallback = callback;
        }

        public SpoofEntry(PerClientBoolProxy callback) : this()
        {
            CallbackType = CallbackKind.Bool;
            BoolCallback = callback;
        }

        public SpoofEntry(PerClientVectorProxy callback) : this()
        {
            CallbackType   = CallbackKind.Vector;
            VectorCallback = callback;
        }

        public bool HasCallback => CallbackType != CallbackKind.None;
    }

    private static readonly ConcurrentDictionary<(string ser, string field, int entityIndex), SpoofEntry> _registry = new();

    public static void SetSpoof(string serializerName, string fieldName, int value)
        => _registry[(serializerName, fieldName, -1)] = new SpoofEntry(value);

    public static void SetCallback(string serializerName, string fieldName, PerClientIntProxy callback)
        => _registry[(serializerName, fieldName, -1)] = new SpoofEntry(callback);

    public static void SetCallback(string serializerName, string fieldName, PerClientFloatProxy callback)
        => _registry[(serializerName, fieldName, -1)] = new SpoofEntry(callback);

    public static void SetCallback(string serializerName, string fieldName, PerClientBoolProxy callback)
        => _registry[(serializerName, fieldName, -1)] = new SpoofEntry(callback);

    public static void SetCallback(string serializerName, string fieldName, PerClientVectorProxy callback)
        => _registry[(serializerName, fieldName, -1)] = new SpoofEntry(callback);

    public static void ClearCallback(string serializerName, string fieldName)
        => _registry.TryRemove((serializerName, fieldName, -1), out _);

    public static void SetEntitySpoof(int entityIndex, string serializerName, string fieldName, int value)
        => _registry[(serializerName, fieldName, entityIndex)] = new SpoofEntry(value);

    public static void SetEntityCallback(int entityIndex, string serializerName, string fieldName, PerClientIntProxy callback)
        => _registry[(serializerName, fieldName, entityIndex)] = new SpoofEntry(callback);

    public static void SetEntityCallback(int entityIndex, string serializerName, string fieldName, PerClientFloatProxy callback)
        => _registry[(serializerName, fieldName, entityIndex)] = new SpoofEntry(callback);

    public static void SetEntityCallback(int entityIndex, string serializerName, string fieldName, PerClientBoolProxy callback)
        => _registry[(serializerName, fieldName, entityIndex)] = new SpoofEntry(callback);

    public static void SetEntityCallback(int entityIndex, string serializerName, string fieldName, PerClientVectorProxy callback)
        => _registry[(serializerName, fieldName, entityIndex)] = new SpoofEntry(callback);

    public static void ClearEntityRegistration(int entityIndex, string serializerName, string fieldName)
        => _registry.TryRemove((serializerName, fieldName, entityIndex), out _);

    public static void ClearAll()
        => _registry.Clear();

    // CFieldPath* filled by GetBitRange; valid for the BitCopyPrimitive call that follows on this thread.
    [ThreadStatic]
    private static nint _currentFieldPath;

    // CFlattenedSerializer* captured by the WriteFieldList shim (rdi).
    [ThreadStatic]
    private static nint _currentSerializer;

    // Entity index from WriteDeltaEntity ctx+0x34; -1 if not captured.
    [ThreadStatic]
    private static int _currentEntityIndex;

    // To-snapshot CFrameSnapshotEntry* (ctx+0x90), data regions at +0x30 — used by the live-struct path.
    [ThreadStatic]
    private static nint _currentSnapshotPtr;

    private static int                                          _diagCount;
    private const  int                                          MaxDiagCount = 25;
    private static readonly ConcurrentDictionary<(string, string), byte> _diagSeen = new();

    private static int       _logCount;
    private const  int       MaxLogCount = 20;

    private static IDetourHook? _getBitRangeHook;
    private static nint         _getBitRangeTrampoline;
    private static IDetourHook? _valueCopyHook;
    private static nint         _valueCopyTrampoline;
    private static IDetourHook? _wflShimHook;
    private static nint         _wflShimTrampoline;
    private static IDetourHook? _wdeEntityCaptureHook;
    private static nint         _wdeEntityCaptureTrampoline;

    private static ILogger? _logger;

    public static nint GetBitRangeAddr;
    public static nint ValueCopyAddr;
    public static nint WriteFieldListAddr;
    public static nint WdeAddr;

    // Encoder identities resolved from gamedata (set by SendProxyModule before Install).
    private static nint   _registryAddr;
    private static nint[] _bucketAddrs = Array.Empty<nint>();
    private static nint   _encodeInt32Addr;

    // Bucket-index parallel to _bucketAddrs entries (engine encoder-registry bucket numbers).
    private static readonly int[] BucketIndices = { 1, 2, 3, 4, 7 };

    // fn pointer -> FieldType, built once at Install from the gamedata-resolved bucket handler bases.
    private static Dictionary<nint, FieldType>? _encoderTypes;
    private static readonly object _encoderTypesLock = new();

    /// <summary>
    ///     Provide the gamedata-resolved encoder identities used for classification:
    ///     the registry table base, the per-bucket handler array bases (parallel to
    ///     <see cref="BucketIndices"/>), and the standalone int32 encoder fn (cross-check).
    /// </summary>
    public static void SetEncoderResolution(nint registryAddr, nint[] bucketAddrs, nint encodeInt32Addr)
    {
        _registryAddr    = registryAddr;
        _bucketAddrs     = bucketAddrs ?? Array.Empty<nint>();
        _encodeInt32Addr = encodeInt32Addr;
    }

    public static bool Install(InterfaceBridge bridge, ILogger logger)
    {
        _logger = logger;

        if (!ValidateAddresses(logger))
        {
            return false;
        }

        if (_encoderTypes is null)
        {
            lock (_encoderTypesLock)
            {
                if (_encoderTypes is null)
                {
                    _encoderTypes = BuildEncoderTypeMap(logger);
                }
            }
        }

        if (_wflShimHook is null)
        {
            var wflHook = bridge.HookManager.CreateDetourHook();
            var wflHookFn = (nint) (delegate* unmanaged[Cdecl]<
                nint, nint, nint, nint, nint, uint,
                uint, nint, uint,
                nint>) &WflShim;
            wflHook.Prepare(WriteFieldListAddr, wflHookFn);

            if (!wflHook.Install())
            {
                logger.LogWarning("FieldSubstitution: WriteFieldList shim Install() failed");

                return false;
            }

            _wflShimHook       = wflHook;
            _wflShimTrampoline = wflHook.Trampoline;
            logger.LogInformation("FieldSubstitution: WriteFieldList shim installed @ 0x{Addr:X}", WriteFieldListAddr);
        }

        if (_getBitRangeHook is null)
        {
            var gbrHook   = bridge.HookManager.CreateDetourHook();
            var gbrHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, uint, void>) &GetBitRangeHook;
            gbrHook.Prepare(GetBitRangeAddr, gbrHookFn);

            if (!gbrHook.Install())
            {
                logger.LogWarning("FieldSubstitution: GetBitRange hook Install() failed");
                _wflShimHook?.Uninstall();
                _wflShimHook?.Dispose();
                _wflShimHook = null;

                return false;
            }

            _getBitRangeHook       = gbrHook;
            _getBitRangeTrampoline = gbrHook.Trampoline;
            logger.LogInformation("FieldSubstitution: GetBitRange hook installed @ 0x{Addr:X}", GetBitRangeAddr);
        }

        if (_valueCopyHook is null)
        {
            var vcHook   = bridge.HookManager.CreateDetourHook();
            var vcHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) &ValueCopyHook;
            vcHook.Prepare(ValueCopyAddr, vcHookFn);

            if (!vcHook.Install())
            {
                logger.LogWarning("FieldSubstitution: value-copy hook Install() failed");
                _getBitRangeHook?.Uninstall();
                _getBitRangeHook?.Dispose();
                _getBitRangeHook = null;
                _wflShimHook?.Uninstall();
                _wflShimHook?.Dispose();
                _wflShimHook = null;

                return false;
            }

            _valueCopyHook       = vcHook;
            _valueCopyTrampoline = vcHook.Trampoline;
            logger.LogInformation("FieldSubstitution: value-copy hook installed @ 0x{Addr:X}", ValueCopyAddr);
        }

        if (_wdeEntityCaptureHook is null && WdeAddr != 0)
        {
            var wdeHook   = bridge.HookManager.CreateDetourHook();
            var wdeHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) &WdeEntityCaptureHook;
            wdeHook.Prepare(WdeAddr, wdeHookFn);

            if (wdeHook.Install())
            {
                _wdeEntityCaptureHook       = wdeHook;
                _wdeEntityCaptureTrampoline = wdeHook.Trampoline;
                logger.LogInformation("FieldSubstitution: WriteDeltaEntity entity-index capture installed @ 0x{Addr:X}", WdeAddr);
            }
            else
            {
                logger.LogWarning("FieldSubstitution: WriteDeltaEntity entity-index capture Install() failed — entityIndex will be -1 in callbacks");
                wdeHook.Dispose();
            }
        }

        Interlocked.Exchange(ref _logCount, 0);
        Interlocked.Exchange(ref _diagCount, 0);
        _diagSeen.Clear();

        return true;
    }

    public static void Uninstall()
    {
        Mode = SubstitutionMode.Off;

        _valueCopyHook?.Uninstall();
        _valueCopyHook?.Dispose();
        _valueCopyHook       = null;
        _valueCopyTrampoline = 0;

        _getBitRangeHook?.Uninstall();
        _getBitRangeHook?.Dispose();
        _getBitRangeHook       = null;
        _getBitRangeTrampoline = 0;

        _wflShimHook?.Uninstall();
        _wflShimHook?.Dispose();
        _wflShimHook       = null;
        _wflShimTrampoline = 0;

        _wdeEntityCaptureHook?.Uninstall();
        _wdeEntityCaptureHook?.Dispose();
        _wdeEntityCaptureHook       = null;
        _wdeEntityCaptureTrampoline = 0;

        _logger?.LogInformation("FieldSubstitution: all hooks uninstalled");
        _logger = null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WflShim(
        nint a,
        nint b, nint c, nint d, nint e,
        uint p6,
        uint p7, nint p8, uint p9)
    {
        _currentSerializer = a;
        var result = ((delegate* unmanaged[Cdecl]<
            nint, nint, nint, nint, nint, uint,
            uint, nint, uint,
            nint>) _wflShimTrampoline)(a, b, c, d, e, p6, p7, p8, p9);
        _currentSerializer = 0;

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WdeEntityCaptureHook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        var entityIndex = -1;
        var snapshotPtr = (nint) 0;

        if (NativeUtil.IsUserPtr(b))
        {
            try
            {
                entityIndex = *(int*) (b + 0x34);
            }
            catch
            {
                // Leave entityIndex at -1 if the read faults.
            }

            try
            {
                var raw = *(nint*) (b + 0x90);
                if (NativeUtil.IsUserPtr(raw))
                {
                    snapshotPtr = raw;
                }
            }
            catch
            {
                // Leave snapshotPtr at 0 if the read faults.
            }
        }

        _currentEntityIndex = entityIndex;
        _currentSnapshotPtr = snapshotPtr;
        nint result;

        try
        {
            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)
                _wdeEntityCaptureTrampoline)(a, b, c, d, e, f);
        }
        finally
        {
            _currentEntityIndex = -1;
            _currentSnapshotPtr = 0;
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GetBitRangeHook(nint pathOut, nint table, uint registryIndex)
    {
        ((delegate* unmanaged[Cdecl]<nint, nint, uint, void>) _getBitRangeTrampoline)(pathOut, table, registryIndex);
        _currentFieldPath = pathOut;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte ValueCopyHook(nint dst, nint src, uint bitcount)
    {
        var mode = (SubstitutionMode) _mode;
        if (mode == SubstitutionMode.Off)
        {
            goto Passthrough;
        }

        var serPtr = _currentSerializer;
        if (!NativeUtil.IsUserPtr(serPtr))
        {
            goto Passthrough;
        }

        try
        {
            var serName   = NativeUtil.ReadShortAscii(*(nint*) (serPtr + 0x00), 48);
            var fieldName = ResolveFieldName(serPtr, _currentFieldPath, out var leafRec);

            if (fieldName.Length > 0 && _logger is { } diagLog)
            {
                var key = (serName, fieldName);
                if (_diagSeen.TryAdd(key, 0))
                {
                    var n = Interlocked.Increment(ref _diagCount);
                    if (n <= MaxDiagCount)
                    {
                        try
                        {
                            var hdr   = _currentFieldPath;
                            var count = (hdr != 0) ? *(short*) (hdr + 0x18) : (short) 0;
                            var i0    = (count > 0) ? *(short*) (hdr + 0x00) : (short) -1;
                            var i1    = (count > 1) ? *(short*) (hdr + 0x02) : (short) -1;
                            var i2    = (count > 2) ? *(short*) (hdr + 0x04) : (short) -1;
                            diagLog.LogInformation(
                                "WFLD#{N} ser=\"{Ser}\" count={Count} idx=[{I0},{I1},{I2}] name=\"{Name}\"",
                                n, serName, count, i0, i1, i2, fieldName);
                        }
                        catch
                        {
                            // Diagnostics only — never let logging faults escape the hook.
                        }
                    }
                }
            }

            if (fieldName.Length == 0)
            {
                goto Passthrough;
            }

            var client      = RecipientCapture.CurrentClient;
            var entityIndex = _currentEntityIndex;

            SpoofEntry reg;
            bool       hasReg;
            if (entityIndex >= 0 && _registry.TryGetValue((serName, fieldName, entityIndex), out reg))
            {
                hasReg = true;
            }
            else if (_registry.TryGetValue((serName, fieldName, -1), out reg))
            {
                hasReg = true;
            }
            else
            {
                hasReg = false;
            }

            if (!hasReg)
            {
                goto Passthrough;
            }

            var fieldType = Classify(leafRec);
            var ln        = Interlocked.Increment(ref _logCount);

            if (ln <= MaxLogCount && _logger is { } diagSubstLog)
            {
                try
                {
                    var regEntIdx = _registry.TryGetValue((serName, fieldName, entityIndex), out _) && entityIndex >= 0
                        ? entityIndex
                        : -1;
                    diagSubstLog.LogInformation(
                        "SUBST ent={CurEnt} ser=\"{Ser}\" field=\"{Field}\" client=0x{Client:X} target={RegEnt}",
                        entityIndex, serName, fieldName, client, regEntIdx);
                }
                catch
                {
                    // Diagnostics only.
                }
            }

            if (mode == SubstitutionMode.Verify)
            {
                var cursorBefore = (dst != 0) ? *(int*) (dst + 0x10) : -1;
                var result       = CallOriginal(dst, src, bitcount);
                var cursorAfter  = (dst != 0) ? *(int*) (dst + 0x10) : -1;

                if (ln <= MaxLogCount && _logger is { } log)
                {
                    log.LogInformation(
                        "SUBST-VERIFY field=\"{Ser}::{Field}\" type={Type} client=0x{Client:X} ent={Ent} bitcount={Bits} "
                        + "cursorBefore={Before} cursorAfter={After} (fake would be {Fake})",
                        serName, fieldName, fieldType, client, entityIndex, bitcount, cursorBefore, cursorAfter, reg.SpoofValue);
                }

                return result;
            }

            bool shouldSubstitute;
            var  effectiveFake = reg.HasSpoof ? reg.SpoofValue : 0;
            var  vx = 0f;
            var  vy = 0f;
            var  vz = 0f;

            if (!reg.HasCallback)
            {
                shouldSubstitute = true;
            }
            else
            {
                try
                {
                    switch (reg.CallbackType)
                    {
                        case CallbackKind.Int:
                            shouldSubstitute = reg.IntCallback!(client, entityIndex, ref effectiveFake);
                            break;

                        case CallbackKind.Float:
                            var fval = 0f;
                            shouldSubstitute = reg.FloatCallback!(client, entityIndex, ref fval);
                            effectiveFake    = BitConverter.SingleToInt32Bits(fval);
                            break;

                        case CallbackKind.Bool:
                            var bval = false;
                            shouldSubstitute = reg.BoolCallback!(client, entityIndex, ref bval);
                            effectiveFake    = bval ? 1 : 0;
                            break;

                        case CallbackKind.Vector:
                            shouldSubstitute = reg.VectorCallback!(client, entityIndex, ref vx, ref vy, ref vz);
                            break;

                        default:
                            shouldSubstitute = false;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    if (ln <= MaxLogCount && _logger is { } errLog)
                    {
                        try
                        {
                            errLog.LogWarning(ex,
                                "SUBST-FAKE per-client callback threw for \"{Ser}::{Field}\" "
                                + "client=0x{Client:X} ent={Ent} — passing through original",
                                serName, fieldName, client, entityIndex);
                        }
                        catch
                        {
                            // Diagnostics only.
                        }
                    }

                    return CallOriginal(dst, src, bitcount);
                }
            }

            if (!shouldSubstitute)
            {
                return CallOriginal(dst, src, bitcount);
            }

            // Default-safe: only substitute encoder families we can re-emit; anything else passes through.
            if (fieldType == FieldType.Unsupported)
            {
                if (ln <= MaxLogCount && _logger is { } skipLog)
                {
                    skipLog.LogWarning(
                        "SUBST-FAKE field=\"{Ser}::{Field}\" classified Unsupported — passing through "
                        + "(registered but not a substitutable type)", serName, fieldName);
                }

                return CallOriginal(dst, src, bitcount);
            }

            var isLiveStructType =
                fieldType == FieldType.Coord3
                || fieldType == FieldType.Normal3
                || fieldType == FieldType.CoordIntegral3
                || fieldType == FieldType.QuantizedFloat;

            if (isLiveStructType)
            {
                var hasCompatibleCallback =
                    !reg.HasCallback
                    || reg.CallbackType == CallbackKind.Vector
                    || reg.CallbackType == CallbackKind.Float;

                if (!hasCompatibleCallback)
                {
                    if (ln <= MaxLogCount && _logger is { } cbWarnLog)
                    {
                        cbWarnLog.LogWarning(
                            "SUBST-FAKE field=\"{Ser}::{Field}\" live-struct type={Type} "
                            + "but callback={CbKind} is not Vector/Float — passing through",
                            serName, fieldName, fieldType, reg.CallbackType);
                    }

                    return CallOriginal(dst, src, bitcount);
                }

                if (!TryGetLiveValuePtr(leafRec, out var liveValuePtr))
                {
                    if (ln <= MaxLogCount && _logger is { } noVpLog)
                    {
                        noVpLog.LogWarning(
                            "SUBST-FAKE field=\"{Ser}::{Field}\" live-struct type={Type} "
                            + "— live valuePtr unavailable (WriteDeltaEntity hook absent or snapshot invalid) "
                            + "— passing through", serName, fieldName, fieldType);
                    }

                    return CallOriginal(dst, src, bitcount);
                }

                var liveScratch = stackalloc byte[LiveScratchSize];

                for (var bi = 0; bi < LiveScratchSize; bi++)
                {
                    liveScratch[bi] = 0;
                }

                for (var bi = 0; bi < LiveScratchSize; bi++)
                {
                    liveScratch[bi] = *(byte*) (liveValuePtr + bi);
                }

                // Patch only the float component(s) the callback drove; preserve [+0x28] count and flags.
                switch (reg.CallbackType)
                {
                    case CallbackKind.Vector:
                        ((float*) liveScratch)[0] = vx;
                        ((float*) liveScratch)[1] = vy;
                        ((float*) liveScratch)[2] = vz;
                        break;

                    case CallbackKind.Float:
                        ((float*) liveScratch)[0] = BitConverter.Int32BitsToSingle(effectiveFake);
                        break;

                    default:
                        ((float*) liveScratch)[0] = BitConverter.Int32BitsToSingle(effectiveFake);
                        break;
                }

                var liveFi        = *(nint*) (leafRec + 0x00);
                var liveDispatch  = *(nint*) (liveFi + 0x38);
                var liveEncFn     = NativeUtil.IsUserPtr(liveDispatch) ? *(nint*) liveDispatch : 0;
                var liveParamOff  = *(byte*) (liveFi + 0xC9);
                var liveParamsPtr = (liveParamOff == 0xFF) ? 0 : (*(nint*) (liveFi + 0x40) + liveParamOff);

                if (!NativeUtil.IsUserPtr(liveEncFn))
                {
                    goto Passthrough;
                }

                var liveSavedCursor = *(int*) (dst + 0x10);
                var liveOrigResult  = CallOriginal(dst, src, bitcount);
                var liveAfterCursor = *(int*) (dst + 0x10);
                *(int*) (dst + 0x10) = liveSavedCursor;

                try
                {
                    ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, void>)
                        liveEncFn)(dst, liveFi, liveParamsPtr, (nint) liveScratch, 0u);
                }
                catch
                {
                    *(int*) (dst + 0x10) = liveAfterCursor;

                    return liveOrigResult;
                }

                if (ln <= MaxLogCount && _logger is { } liveLog)
                {
                    liveLog.LogInformation(
                        "SUBST-FAKE(live) field=\"{Ser}::{Field}\" type={Type} "
                        + "client=0x{Client:X} ent={Ent} bitcount={Bits} savedCursor={Saved} "
                        + "vx={Vx} vy={Vy} vz={Vz}",
                        serName, fieldName, fieldType, client, entityIndex, bitcount,
                        liveSavedCursor, vx, vy, vz);
                }

                return liveOrigResult;
            }

            // Scalar / synthesized-vector path: save cursor, call original, rewind, re-emit via the
            // field's own engine encoder (dispatch slot 0 at fieldInfo+0x38) with a synthesized scratch.
            var savedCursor    = *(int*) (dst + 0x10);
            var originalResult = CallOriginal(dst, src, bitcount);
            var afterOriginal  = *(int*) (dst + 0x10);

            *(int*) (dst + 0x10) = savedCursor;

            var fieldInfo = *(nint*) (leafRec + 0x00);
            var dispatch  = *(nint*) (fieldInfo + 0x38);
            var encoderFn = NativeUtil.IsUserPtr(dispatch) ? *(nint*) dispatch : 0;
            var paramOff  = *(byte*) (fieldInfo + 0xC9);
            var paramsPtr = (paramOff == 0xFF) ? 0 : (*(nint*) (fieldInfo + 0x40) + paramOff);

            if (!NativeUtil.IsUserPtr(encoderFn))
            {
                *(int*) (dst + 0x10) = afterOriginal;

                return originalResult;
            }

            var scratch = stackalloc byte[16];
            switch (fieldType)
            {
                case FieldType.Int32:
                case FieldType.Int64:
                case FieldType.Fixed32:
                case FieldType.Fixed64:
                    *(long*) scratch = effectiveFake;
                    break;

                case FieldType.UInt32:
                    *(ulong*) scratch = (uint) effectiveFake;
                    break;

                case FieldType.Bool:
                    *scratch = (byte) (effectiveFake != 0 ? 1 : 0);
                    break;

                case FieldType.Float32:
                    *(double*) scratch = BitConverter.Int32BitsToSingle(effectiveFake);
                    break;

                case FieldType.QAngle3:
                case FieldType.Vector3:
                    ((float*) scratch)[0] = vx;
                    ((float*) scratch)[1] = vy;
                    ((float*) scratch)[2] = vz;
                    break;

                default:
                    *(int*) (dst + 0x10) = afterOriginal;

                    return originalResult;
            }

            try
            {
                ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, void>)
                    encoderFn)(dst, fieldInfo, paramsPtr, (nint) scratch, 0u);
            }
            catch
            {
                *(int*) (dst + 0x10) = afterOriginal;

                return originalResult;
            }

            if (ln <= MaxLogCount && _logger is { } fakeLog)
            {
                fakeLog.LogInformation(
                    "SUBST-FAKE field=\"{Ser}::{Field}\" type={Type} client=0x{Client:X} ent={Ent} bitcount={Bits} "
                    + "savedCursor={Saved} effectiveFake={Fake}",
                    serName, fieldName, fieldType, client, entityIndex, bitcount, savedCursor, effectiveFake);
            }

            return originalResult;
        }
        catch
        {
            // Never throw out of an unmanaged callback.
        }

        Passthrough:
        return CallOriginal(dst, src, bitcount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte CallOriginal(nint dst, nint src, uint bitcount)
        => ((delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) _valueCopyTrampoline)(dst, src, bitcount);

    private static string ResolveFieldName(nint serializer, nint hdr, out nint leafRec)
    {
        leafRec = 0;
        if (!NativeUtil.IsUserPtr(serializer) || !NativeUtil.IsUserPtr(hdr))
        {
            return string.Empty;
        }

        try
        {
            var count = *(short*) (hdr + 0x18);
            if (count <= 0 || count > 7)
            {
                return string.Empty;
            }

            nint idxArr;
            if (*(byte*) (hdr + 0x1A) != 0)
            {
                idxArr = *(nint*) hdr;
                if (!NativeUtil.IsUserPtr(idxArr))
                {
                    return string.Empty;
                }
            }
            else
            {
                idxArr = hdr;
            }

            var idx0 = *(short*) (idxArr + 0 * 2);
            if (idx0 == 0x7FFF)
            {
                return string.Empty;
            }

            var arr0 = *(nint*) (serializer + 0x30);
            if (!NativeUtil.IsUserPtr(arr0))
            {
                return string.Empty;
            }

            var rec = arr0 + idx0 * 0x2E;

            for (var k = 1; k < count; k++)
            {
                var idxK = *(short*) (idxArr + k * 2);
                if (idxK == 0x7FFF)
                {
                    break;
                }

                var child = *(nint*) (rec + 0x08);
                if (!NativeUtil.IsUserPtr(child))
                {
                    return string.Empty;
                }

                var arrK = *(nint*) (child + 0x30);
                if (!NativeUtil.IsUserPtr(arrK))
                {
                    return string.Empty;
                }

                rec = arrK + idxK * 0x2E;
            }

            var pInfo = *(nint*) (rec + 0x00);
            if (!NativeUtil.IsUserPtr(pInfo))
            {
                return string.Empty;
            }

            leafRec = rec;

            return NativeUtil.ReadShortAscii(*(nint*) (pInfo + 0x08), 48);
        }
        catch
        {
            leafRec = 0;

            return string.Empty;
        }
    }

    // Classify the field's encoder family by comparing its live dispatch fn (slot 0 of the dispatch
    // object at fieldInfo+0x38) against the gamedata-resolved encoder map. Any fault or unknown fn
    // yields Unsupported, so the caller passes through and never corrupts bits.
    private static FieldType Classify(nint leafRec)
    {
        if (!NativeUtil.IsUserPtr(leafRec))
        {
            return FieldType.Unsupported;
        }

        var map = _encoderTypes;
        if (map is null)
        {
            return FieldType.Unsupported;
        }

        try
        {
            var fieldInfo = *(nint*) (leafRec + 0x00);
            if (!NativeUtil.IsUserPtr(fieldInfo))
            {
                return FieldType.Unsupported;
            }

            var handler = *(nint*) (fieldInfo + 0x38);
            if (!NativeUtil.IsUserPtr(handler))
            {
                return FieldType.Unsupported;
            }

            var encoderFn = *(nint*) (handler + 0x30);
            if (!NativeUtil.IsUserPtr(encoderFn))
            {
                return FieldType.Unsupported;
            }

            return map.TryGetValue(encoderFn, out var t) ? t : FieldType.Unsupported;
        }
        catch
        {
            return FieldType.Unsupported;
        }
    }

    // Largest quantized value struct layout observed in RE fits in 0x30 bytes.
    private const int LiveScratchSize = 0x30;

    // Data-region array at snapshotPtr+0x30 holds at most 15 entries (EncodeField stack buffer width).
    private const int MaxRegionId = 14;

    // Reconstruct the live entity valuePtr for a quantized-float field from the captured snapshot:
    //   valuePtr = *(nint*)(snapshotPtr + 0x30 + regionId*8) + *(ushort*)(fieldInfo + 0x20)
    //   regionId = *(byte*)(leafRec + 0x2C)
    // RE evidence: docs/REVERSE_ENGINEERING.md §15. IsUserPtr-gated; returns false on any failure.
    private static bool TryGetLiveValuePtr(nint leafRec, out nint valuePtr)
    {
        valuePtr = 0;

        var snapshotPtr = _currentSnapshotPtr;
        if (!NativeUtil.IsUserPtr(snapshotPtr))
        {
            return false;
        }

        try
        {
            var fieldInfo = *(nint*) (leafRec + 0x00);
            if (!NativeUtil.IsUserPtr(fieldInfo))
            {
                return false;
            }

            var regionId = (uint) *(byte*) (leafRec + 0x2C);
            if (regionId > MaxRegionId)
            {
                return false;
            }

            var fieldOffset    = (uint) *(ushort*) (fieldInfo + 0x20);
            var regionArrayPtr = snapshotPtr + 0x30 + (nint) (regionId * 8);
            if (!NativeUtil.IsUserPtr(regionArrayPtr))
            {
                return false;
            }

            var dataRegionBase = *(nint*) regionArrayPtr;
            if (!NativeUtil.IsUserPtr(dataRegionBase))
            {
                return false;
            }

            var candidate = dataRegionBase + (nint) fieldOffset;
            if (!NativeUtil.IsUserPtr(candidate))
            {
                return false;
            }

            valuePtr = candidate;

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Build the fn-pointer -> FieldType map once at Install, anchored on the gamedata-resolved bucket
    // handler bases (no in-C# RegistryAddr + b*16 stride assumption for the handlers). Each bucket's
    // encoder entries (stride 0x80) carry the encoder-name at +0x00 and the fn at +0x30; the (bucket,
    // name) pair determines the FieldType, exactly as the engine's own InitFakeField wiring does. The
    // bucket-1 default fn is cross-checked against the standalone EncodeInt32 sig (a warning logs on
    // mismatch). These reads happen once here, not on the per-field hot path (Classify is a dict lookup).
    // Returns a non-null (possibly empty) map; an empty map => all fields Unsupported (safe passthrough).
    private static Dictionary<nint, FieldType> BuildEncoderTypeMap(ILogger logger)
    {
        var map = new Dictionary<nint, FieldType>();

        if (_bucketAddrs.Length == 0)
        {
            logger.LogWarning("FieldSubstitution: no encoder bucket bases resolved — encoder map will be empty (all fields Unsupported)");

            return map;
        }

        try
        {
            for (var i = 0; i < _bucketAddrs.Length && i < BucketIndices.Length; i++)
            {
                var bucket  = BucketIndices[i];
                var handler = _bucketAddrs[i];
                if (!NativeUtil.IsUserPtr(handler))
                {
                    continue;
                }

                var count = ReadBucketCount(bucket);
                if (count <= 0 || count > 32)
                {
                    continue;
                }

                for (var e = 0; e < count; e++)
                {
                    var entry = handler + e * 0x80;

                    nint namePtr;
                    nint fn;
                    try
                    {
                        namePtr = *(nint*) (entry + 0x00);
                        fn      = *(nint*) (entry + 0x30);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!NativeUtil.IsUserPtr(fn))
                    {
                        continue;
                    }

                    var name = NativeUtil.ReadShortAscii(namePtr, 32);
                    var type = ClassifyEntry(bucket, name);
                    if (type == FieldType.Unsupported)
                    {
                        continue;
                    }

                    if (type == FieldType.Int32
                        && NativeUtil.IsUserPtr(_encodeInt32Addr)
                        && fn != _encodeInt32Addr)
                    {
                        logger.LogWarning(
                            "FieldSubstitution: bucket-1 default fn=0x{Fn:X} does not match the gamedata "
                            + "EncodeInt32 sig (0x{Sig:X}) — int32 classification may be stale for this build",
                            fn, _encodeInt32Addr);
                    }

                    map[fn] = type;
                    logger.LogInformation(
                        "FieldSubstitution encoder: bucket={B} name=\"{Name}\" fn=0x{Fn:X} -> {Type}",
                        bucket, name, fn, type);
                }
            }

            logger.LogInformation("FieldSubstitution: encoder type map built — {Count} entries", map.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FieldSubstitution: BuildEncoderTypeMap faulted — partial or empty map");
        }

        return map;
    }

    // Entry count for a bucket = *(int*)(RegistryAddr + bucket*16 + 0x08). The registry base is gamedata-
    // resolved; we read only the numeric count here. Falls back to a bounded default when unavailable.
    private static int ReadBucketCount(int bucket)
    {
        if (!NativeUtil.IsUserPtr(_registryAddr))
        {
            return 8;
        }

        try
        {
            return *(int*) (_registryAddr + bucket * 16 + 0x08);
        }
        catch
        {
            return 0;
        }
    }

    // Map (bucket, encoder-name) -> FieldType, matching the engine's encoder-registry semantics.
    private static FieldType ClassifyEntry(int bucket, string name)
    {
        switch (bucket)
        {
            case 1:
                if (name == "default")
                {
                    return FieldType.Int32;
                }

                if (name == "fixed32")
                {
                    return FieldType.Fixed32;
                }

                if (name == "fixed64")
                {
                    return FieldType.Fixed64;
                }

                return FieldType.Unsupported;

            case 2:
                if (name == "default")
                {
                    return FieldType.UInt32;
                }

                if (name == "fixed32")
                {
                    return FieldType.Fixed32;
                }

                if (name == "fixed64")
                {
                    return FieldType.Fixed64;
                }

                return FieldType.Unsupported;

            case 3:
                if (name == "qangle")
                {
                    return FieldType.QAngle3;
                }

                if (name == "vector3")
                {
                    return FieldType.Vector3;
                }

                if (name == "coord")
                {
                    return FieldType.Coord3;
                }

                if (name == "normal")
                {
                    return FieldType.Normal3;
                }

                if (name == "coord_integral")
                {
                    return FieldType.CoordIntegral3;
                }

                if (name == "quantized")
                {
                    return FieldType.QuantizedFloat;
                }

                return FieldType.Unsupported;

            case 4:
                return name == "default" ? FieldType.Float32 : FieldType.Unsupported;

            case 7:
                return name == "default" ? FieldType.Bool : FieldType.Unsupported;

            default:
                return FieldType.Unsupported;
        }
    }

    private static bool ValidateAddresses(ILogger logger)
    {
        var ok = true;
        if (GetBitRangeAddr == 0)
        {
            logger.LogWarning("FieldSubstitution: GetBitRange address not resolved");
            ok = false;
        }

        if (ValueCopyAddr == 0)
        {
            logger.LogWarning("FieldSubstitution: ValueCopy address not resolved");
            ok = false;
        }

        if (WriteFieldListAddr == 0)
        {
            logger.LogWarning("FieldSubstitution: WriteFieldList address not resolved");
            ok = false;
        }

        return ok;
    }
}
