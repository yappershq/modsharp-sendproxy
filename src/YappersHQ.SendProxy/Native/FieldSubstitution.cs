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
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Native;

// Per-client per-field value substitution. WriteFieldList runs once per changed field per client on an
// engine send worker thread; it calls GetBitRange (resolves the CFieldPath of the field being copied)
// then BitCopyPrimitive (copies the field's pre-encoded bits). Substitution rides BitCopyPrimitive: save
// the dst bit cursor, call the original, rewind, then re-emit the substitute value through the field's
// own engine encoder. RE layout/offset details: docs/REVERSE_ENGINEERING.md §8/§9.
//
// Native memory access is guarded by IsUserPtr, never by try/catch — a bad dereference faults the
// process (AccessViolation is not a catchable managed exception), so the pointer guard is the only real
// protection. The few try blocks here wrap genuine managed throw-sites: the unmanaged-callback boundary,
// the user proxy invocation, and the one-time install-time encoder enumeration. All per-field managed
// work (building the substitute buffer) completes BEFORE the native save/rewind/emit sequence, so that
// sequence has no managed throw-site and needs no guard of its own.

internal enum SubstitutionMode
{
    Off,
    Verify,
    Fake,
}

// Network encoder family for a registered field. The Coord3/Normal3/CoordIntegral3/QuantizedFloat set
// requires reading the live value struct from the entity snapshot (the encoder's quant logic must see
// the real float). String re-points the value at a scratch char*; ByteArray hands the encoder a scratch
// {data,count} struct. Unsupported MUST pass through untouched — writing wrong-type bits would corrupt
// the delta for every client.
internal enum FieldType
{
    Unsupported = 0,
    Int32, UInt32, Int64, Bool, Float32, Fixed32, Fixed64,
    QAngle3, Vector3,
    Coord3, Normal3, CoordIntegral3, QuantizedFloat,
    String, ByteArray,
}

internal enum CallbackKind
{
    None = 0,
    Int,
    Float,
    Bool,
    Vector,
    String,
    Bytes,
}

internal static unsafe class FieldSubstitution
{
    private static volatile int _mode = (int) SubstitutionMode.Off;

    public static SubstitutionMode Mode
    {
        get => (SubstitutionMode) _mode;
        set => Interlocked.Exchange(ref _mode, (int) value);
    }

    // A registration holds either a uniform value (one of the Has*/non-null fields) or a per-client typed
    // callback. Key entityIndex: -1 = all entities (global), >= 0 = a specific entity. ValueCopyHook
    // probes the entity-specific entry first, then the global fallback — both lock-free dictionary reads.
    private readonly struct SpoofEntry
    {
        public readonly bool    HasIntSpoof;
        public readonly int     IntSpoof;       // int / uint / bool / fixed / float-bits
        public readonly bool    HasVectorSpoof;
        public readonly Vector3 VectorSpoof;
        public readonly string? StringSpoof;
        public readonly byte[]? BytesSpoof;

        public readonly CallbackKind          CallbackType;
        public readonly PerClientIntProxy?    IntCallback;
        public readonly PerClientFloatProxy?  FloatCallback;
        public readonly PerClientBoolProxy?   BoolCallback;
        public readonly PerClientVectorProxy? VectorCallback;
        public readonly PerClientStringProxy? StringCallback;
        public readonly PerClientBytesProxy?  BytesCallback;

        // Assembly name of the module that owns this callback (null for uniform spoofs, which hold no
        // delegate). Used to purge a consumer's callbacks when its module unloads — invoking a delegate
        // into an unloaded AssemblyLoadContext would crash the server.
        public readonly string? Owner;

        public SpoofEntry(int value) : this()
        {
            HasIntSpoof = true;
            IntSpoof    = value;
        }

        public SpoofEntry(Vector3 value) : this()
        {
            HasVectorSpoof = true;
            VectorSpoof    = value;
        }

        public SpoofEntry(string value) : this()
            => StringSpoof = value;

        public SpoofEntry(byte[] value) : this()
            => BytesSpoof = value;

        public SpoofEntry(PerClientIntProxy callback) : this()
        {
            CallbackType = CallbackKind.Int;
            IntCallback  = callback;
            Owner        = OwnerOf(callback);
        }

        public SpoofEntry(PerClientFloatProxy callback) : this()
        {
            CallbackType  = CallbackKind.Float;
            FloatCallback = callback;
            Owner         = OwnerOf(callback);
        }

        public SpoofEntry(PerClientBoolProxy callback) : this()
        {
            CallbackType = CallbackKind.Bool;
            BoolCallback = callback;
            Owner        = OwnerOf(callback);
        }

        public SpoofEntry(PerClientVectorProxy callback) : this()
        {
            CallbackType   = CallbackKind.Vector;
            VectorCallback = callback;
            Owner          = OwnerOf(callback);
        }

        public SpoofEntry(PerClientStringProxy callback) : this()
        {
            CallbackType   = CallbackKind.String;
            StringCallback = callback;
            Owner          = OwnerOf(callback);
        }

        public SpoofEntry(PerClientBytesProxy callback) : this()
        {
            CallbackType  = CallbackKind.Bytes;
            BytesCallback = callback;
            Owner         = OwnerOf(callback);
        }

        // The defining assembly of the callback target = the consumer module that registered it.
        private static string? OwnerOf(Delegate callback)
            => callback.Method.Module.Assembly.GetName().Name;

        public bool HasCallback => CallbackType != CallbackKind.None;

        // True when this registration's callback kind can drive the given field family. A mismatch (e.g.
        // a string callback on an int field) would write wrong-type bits, so the caller passes through.
        public bool CallbackMatches(FieldType type)
            => CallbackType switch
            {
                CallbackKind.Int    => type is FieldType.Int32 or FieldType.UInt32 or FieldType.Int64 or FieldType.Bool or FieldType.Fixed32 or FieldType.Fixed64,
                CallbackKind.Float  => type is FieldType.Float32 or FieldType.Coord3 or FieldType.Normal3 or FieldType.CoordIntegral3 or FieldType.QuantizedFloat,
                CallbackKind.Bool   => type is FieldType.Bool,
                CallbackKind.Vector => type is FieldType.QAngle3 or FieldType.Vector3 or FieldType.Coord3 or FieldType.Normal3 or FieldType.CoordIntegral3 or FieldType.QuantizedFloat,
                CallbackKind.String => type is FieldType.String,
                CallbackKind.Bytes  => type is FieldType.ByteArray,
                _                   => false,
            };
    }

    private static readonly ConcurrentDictionary<(string ser, string field, int entityIndex), SpoofEntry> _registry = new();

    // -- Registration (called from SendProxyManager on the main thread) ----------------------------

    public static void SetSpoof(string ser, string field, int value)         => _registry[(ser, field, -1)] = new SpoofEntry(value);
    public static void SetSpoof(string ser, string field, Vector3 value)     => _registry[(ser, field, -1)] = new SpoofEntry(value);
    public static void SetSpoof(string ser, string field, string value)      => _registry[(ser, field, -1)] = new SpoofEntry(value);
    public static void SetSpoof(string ser, string field, byte[] value)      => _registry[(ser, field, -1)] = new SpoofEntry(value);

    public static void SetCallback(string ser, string field, PerClientIntProxy cb)    => _registry[(ser, field, -1)] = new SpoofEntry(cb);
    public static void SetCallback(string ser, string field, PerClientFloatProxy cb)  => _registry[(ser, field, -1)] = new SpoofEntry(cb);
    public static void SetCallback(string ser, string field, PerClientBoolProxy cb)   => _registry[(ser, field, -1)] = new SpoofEntry(cb);
    public static void SetCallback(string ser, string field, PerClientVectorProxy cb) => _registry[(ser, field, -1)] = new SpoofEntry(cb);
    public static void SetCallback(string ser, string field, PerClientStringProxy cb) => _registry[(ser, field, -1)] = new SpoofEntry(cb);
    public static void SetCallback(string ser, string field, PerClientBytesProxy cb)  => _registry[(ser, field, -1)] = new SpoofEntry(cb);

    public static void SetEntitySpoof(int ent, string ser, string field, int value)     => _registry[(ser, field, ent)] = new SpoofEntry(value);
    public static void SetEntitySpoof(int ent, string ser, string field, Vector3 value) => _registry[(ser, field, ent)] = new SpoofEntry(value);
    public static void SetEntitySpoof(int ent, string ser, string field, string value)  => _registry[(ser, field, ent)] = new SpoofEntry(value);
    public static void SetEntitySpoof(int ent, string ser, string field, byte[] value)  => _registry[(ser, field, ent)] = new SpoofEntry(value);

    public static void SetEntityCallback(int ent, string ser, string field, PerClientIntProxy cb)    => _registry[(ser, field, ent)] = new SpoofEntry(cb);
    public static void SetEntityCallback(int ent, string ser, string field, PerClientFloatProxy cb)  => _registry[(ser, field, ent)] = new SpoofEntry(cb);
    public static void SetEntityCallback(int ent, string ser, string field, PerClientBoolProxy cb)   => _registry[(ser, field, ent)] = new SpoofEntry(cb);
    public static void SetEntityCallback(int ent, string ser, string field, PerClientVectorProxy cb) => _registry[(ser, field, ent)] = new SpoofEntry(cb);
    public static void SetEntityCallback(int ent, string ser, string field, PerClientStringProxy cb) => _registry[(ser, field, ent)] = new SpoofEntry(cb);
    public static void SetEntityCallback(int ent, string ser, string field, PerClientBytesProxy cb)  => _registry[(ser, field, ent)] = new SpoofEntry(cb);

    public static void ClearGlobal(string ser, string field)            => _registry.TryRemove((ser, field, -1), out _);
    public static void ClearEntity(int ent, string ser, string field)   => _registry.TryRemove((ser, field, ent), out _);
    public static void ClearAll()                                       => _registry.Clear();

    // Remove every callback registration owned by an unloading module. A delegate into an unloaded
    // AssemblyLoadContext would crash the server when the send path invokes it, so this is called from
    // OnLibraryDisconnect. Uniform spoofs (Owner == null) hold no delegate and are left untouched.
    public static int PurgeOwner(string moduleName)
    {
        var removed = 0;
        foreach (var kv in _registry)
        {
            if (kv.Value.Owner == moduleName && _registry.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    // Drop every registration scoped to a specific entity index (called from OnEntityDeleted — indices
    // are reused after disconnect/round restart, so stale entity-scoped registrations must not persist).
    public static void ClearEntityIndex(int entityIndex)
    {
        if (entityIndex < 0)
        {
            return;
        }

        foreach (var key in _registry.Keys)
        {
            if (key.entityIndex == entityIndex)
            {
                _registry.TryRemove(key, out _);
            }
        }
    }

    public static bool IsHooked(string ser, string field)
    {
        foreach (var key in _registry.Keys)
        {
            if (key.ser == ser && key.field == field)
            {
                return true;
            }
        }

        return false;
    }

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

    private static int       _diagCount;
    private const  int       MaxDiagCount = 25;
    private static readonly ConcurrentDictionary<(string, string), byte> _diagSeen = new();

    private static int      _substLogCount;
    private const  int      MaxSubstLog = 40;

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
    private static readonly int[] BucketIndices = { 1, 2, 3, 4, 5, 6, 7 };

    // fn pointer -> FieldType, built once at Install from the gamedata-resolved bucket handler bases.
    private static Dictionary<nint, FieldType>? _encoderTypes;
    private static readonly object _encoderTypesLock = new();

    /// <summary>
    ///     Provide the gamedata-resolved encoder identities used for classification: the registry table
    ///     base, the per-bucket handler array bases (parallel to <see cref="BucketIndices"/>), and the
    ///     standalone int32 encoder fn (cross-check).
    /// </summary>
    public static void SetEncoderResolution(nint registryAddr, nint[] bucketAddrs, nint encodeInt32Addr)
    {
        _registryAddr    = registryAddr;
        _bucketAddrs     = bucketAddrs ?? Array.Empty<nint>();
        _encodeInt32Addr = encodeInt32Addr;
    }

    /// <summary>
    ///     Build the encoder type map now (idempotent) so classification is ready — and diagnosable — at
    ///     load, instead of lazily on first registration. The detours still install lazily.
    /// </summary>
    public static void PrebuildEncoderMap(ILogger logger)
    {
        if (_encoderTypes is null)
        {
            lock (_encoderTypesLock)
            {
                _encoderTypes ??= BuildEncoderTypeMap(logger);
            }
        }
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
                _encoderTypes ??= BuildEncoderTypeMap(logger);
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
                DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);

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
                DisposeHook(ref _getBitRangeHook, ref _getBitRangeTrampoline);
                DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);

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

        Interlocked.Exchange(ref _diagCount, 0);
        _diagSeen.Clear();

        return true;
    }

    public static void Uninstall()
    {
        Mode = SubstitutionMode.Off;

        DisposeHook(ref _valueCopyHook, ref _valueCopyTrampoline);
        DisposeHook(ref _getBitRangeHook, ref _getBitRangeTrampoline);
        DisposeHook(ref _wflShimHook, ref _wflShimTrampoline);
        DisposeHook(ref _wdeEntityCaptureHook, ref _wdeEntityCaptureTrampoline);

        _logger?.LogInformation("FieldSubstitution: all hooks uninstalled");
        _logger = null;
    }

    private static void DisposeHook(ref IDetourHook? hook, ref nint trampoline)
    {
        hook?.Uninstall();
        hook?.Dispose();
        hook       = null;
        trampoline = 0;
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
            entityIndex = *(int*) (b + 0x34);

            var raw = *(nint*) (b + 0x90);
            if (NativeUtil.IsUserPtr(raw))
            {
                snapshotPtr = raw;
            }
        }

        _currentEntityIndex = entityIndex;
        _currentSnapshotPtr = snapshotPtr;

        var result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)
            _wdeEntityCaptureTrampoline)(a, b, c, d, e, f);

        _currentEntityIndex = -1;
        _currentSnapshotPtr = 0;

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GetBitRangeHook(nint pathOut, nint table, uint registryIndex)
    {
        ((delegate* unmanaged[Cdecl]<nint, nint, uint, void>) _getBitRangeTrampoline)(pathOut, table, registryIndex);
        _currentFieldPath = NativeUtil.IsUserPtr(pathOut) ? pathOut : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte ValueCopyHook(nint dst, nint src, uint bitcount)
    {
        var mode = (SubstitutionMode) _mode;
        if (mode == SubstitutionMode.Off || !NativeUtil.IsUserPtr(_currentSerializer))
        {
            return CallOriginal(dst, src, bitcount);
        }

        // Consume-once: WriteFieldList buffers the path and value streams separately, so a value-copy is
        // not guaranteed to be preceded by a matching GetBitRange. Take the captured path and clear it
        // immediately — a copy without a fresh path passes through rather than reusing a STALE path
        // (which would resolve against the wrong field and walk into unmapped memory → fatal AV).
        var pathPtr = _currentFieldPath;
        _currentFieldPath = 0;
        if (!NativeUtil.IsUserPtr(pathPtr))
        {
            return CallOriginal(dst, src, bitcount);
        }

        try
        {
            var serPtr    = _currentSerializer;
            var serName   = NativeUtil.ReadShortAscii(*(nint*) (serPtr + 0x00), 48);
            var fieldName = ResolveFieldName(serPtr, pathPtr, out var leafRec);
            if (fieldName.Length == 0)
            {
                return CallOriginal(dst, src, bitcount);
            }

            LogDiscoveredField(serName, fieldName);

            var client      = RecipientCapture.CurrentClient;
            var entityIndex = _currentEntityIndex;

            if (!TryGetRegistration(serName, fieldName, entityIndex, out var reg))
            {
                return CallOriginal(dst, src, bitcount);
            }

            var fieldType = Classify(leafRec);
            if (fieldType == FieldType.Unsupported)
            {
                return CallOriginal(dst, src, bitcount);
            }

            // Resolve the substitute value (uniform seed, then optional per-client callback). The callback
            // is the one genuine managed throw-site on this path; a throw is logged and passed through.
            var     intBits        = reg.HasIntSpoof ? reg.IntSpoof : 0;
            var     vec            = reg.HasVectorSpoof ? reg.VectorSpoof : default;
            string? str            = reg.StringSpoof;
            var     bytes          = reg.BytesSpoof;
            bool    substitute;

            if (!reg.HasCallback)
            {
                substitute = true;
            }
            else if (!reg.CallbackMatches(fieldType))
            {
                // A callback registered for the wrong family would write garbage — pass through.
                return CallOriginal(dst, src, bitcount);
            }
            else
            {
                try
                {
                    substitute = InvokeCallback(reg, client, entityIndex, ref intBits, ref vec, ref str, ref bytes);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "SendProxy: per-client callback threw for \"{Ser}::{Field}\" — passing through", serName, fieldName);

                    return CallOriginal(dst, src, bitcount);
                }
            }

            if (!substitute)
            {
                return CallOriginal(dst, src, bitcount);
            }

            if (mode == SubstitutionMode.Verify)
            {
                return VerifyOnly(dst, src, bitcount, serName, fieldName, fieldType, client, entityIndex);
            }

            // Capped diagnostic: confirms a substitution reached the emit step (pre-dance, no throw risk).
            if (_substLogCount < MaxSubstLog && _logger is { } sLog)
            {
                Interlocked.Increment(ref _substLogCount);
                sLog.LogInformation(
                    "SUBST-FAKE \"{Ser}::{Field}\" type={Type} client=0x{Client:X} ent={Ent} bits={Bits} vec=<{X},{Y},{Z}> bitcount={BC}",
                    serName, fieldName, fieldType, client, entityIndex, intBits, vec.X, vec.Y, vec.Z, bitcount);
            }

            // Resolve the field's own engine encoder (dispatch slot 0 at fieldInfo+0x38) and its params.
            var fieldInfo = *(nint*) (leafRec + 0x00);
            var dispatch  = *(nint*) (fieldInfo + 0x38);
            var encoderFn = NativeUtil.IsUserPtr(dispatch) ? *(nint*) dispatch : 0;
            if (!NativeUtil.IsUserPtr(encoderFn))
            {
                return CallOriginal(dst, src, bitcount);
            }

            var paramOff  = *(byte*) (fieldInfo + 0xC9);
            var paramsPtr = (paramOff == 0xFF) ? 0 : (*(nint*) (fieldInfo + 0x40) + paramOff);

            // Build the substitute value pointer in the layout this encoder expects. All allocation /
            // managed work happens here, BEFORE the native save/rewind/emit below — so that sequence is
            // pure native and cannot throw a managed exception mid-rewrite.
            nint valuePtr;
            var  scratch     = stackalloc byte[LiveScratchSize];
            var  stringSlot  = stackalloc nint[1];

            switch (fieldType)
            {
                case FieldType.String:
                {
                    var s       = str ?? string.Empty;
                    var byteLen = Encoding.UTF8.GetByteCount(s);

                    // The scratch lives on the engine send worker thread's stack; cap it so a caller can't
                    // overflow that stack with an oversized value. Oversized -> pass the real value through.
                    if (byteLen > MaxSubstituteBytes)
                    {
                        return CallOriginal(dst, src, bitcount);
                    }

                    var strBuf = stackalloc byte[byteLen + 1];
                    Encoding.UTF8.GetBytes(s, new Span<byte>(strBuf, byteLen));
                    strBuf[byteLen] = 0;
                    *stringSlot     = (nint) strBuf;        // encoder reads *valuePtr as char*
                    valuePtr        = (nint) stringSlot;
                    break;
                }

                case FieldType.ByteArray:
                {
                    var b = bytes ?? Array.Empty<byte>();
                    if (b.Length > MaxSubstituteBytes)
                    {
                        return CallOriginal(dst, src, bitcount);
                    }

                    var buf = stackalloc byte[b.Length];
                    for (var i = 0; i < b.Length; i++)
                    {
                        buf[i] = b[i];
                    }

                    // Encoder reads {+0x00 data*, +0x28 uint count}; LiveScratchSize (0x30) covers it.
                    for (var i = 0; i < LiveScratchSize; i++)
                    {
                        scratch[i] = 0;
                    }

                    *(nint*) (scratch + 0x00)  = (nint) buf;
                    *(uint*) (scratch + 0x28)  = (uint) b.Length;
                    valuePtr                   = (nint) scratch;
                    break;
                }

                case FieldType.Coord3:
                case FieldType.Normal3:
                case FieldType.CoordIntegral3:
                case FieldType.QuantizedFloat:
                {
                    // The quant encoder must see the real live struct; copy it, then patch the float(s).
                    if (!TryGetLiveValuePtr(leafRec, out var liveValuePtr))
                    {
                        return CallOriginal(dst, src, bitcount);
                    }

                    for (var i = 0; i < LiveScratchSize; i++)
                    {
                        scratch[i] = *(byte*) (liveValuePtr + i);
                    }

                    if (reg.CallbackType == CallbackKind.Vector || reg.HasVectorSpoof)
                    {
                        ((float*) scratch)[0] = vec.X;
                        ((float*) scratch)[1] = vec.Y;
                        ((float*) scratch)[2] = vec.Z;
                    }
                    else
                    {
                        ((float*) scratch)[0] = BitConverter.Int32BitsToSingle(intBits);
                    }

                    valuePtr = (nint) scratch;
                    break;
                }

                case FieldType.QAngle3:
                case FieldType.Vector3:
                    ((float*) scratch)[0] = vec.X;
                    ((float*) scratch)[1] = vec.Y;
                    ((float*) scratch)[2] = vec.Z;
                    valuePtr = (nint) scratch;
                    break;

                case FieldType.Float32:
                    *(double*) scratch = BitConverter.Int32BitsToSingle(intBits);
                    valuePtr           = (nint) scratch;
                    break;

                case FieldType.UInt32:
                    *(ulong*) scratch = (uint) intBits;
                    valuePtr          = (nint) scratch;
                    break;

                case FieldType.Bool:
                    *scratch = (byte) (intBits != 0 ? 1 : 0);
                    valuePtr = (nint) scratch;
                    break;

                default: // Int32 / Int64 / Fixed32 / Fixed64
                    *(long*) scratch = intBits;
                    valuePtr         = (nint) scratch;
                    break;
            }

            // Native save / rewind / emit — no managed throw-site between these calls.
            var savedCursor = *(int*) (dst + 0x10);
            var result      = CallOriginal(dst, src, bitcount);
            *(int*) (dst + 0x10) = savedCursor;

            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, void>)
                encoderFn)(dst, fieldInfo, paramsPtr, valuePtr, 0u);

            return result;
        }
        catch
        {
            // Unmanaged-callback boundary: never let a managed exception escape into engine code. Reached
            // only for failures BEFORE the native emit sequence above, so a plain passthrough is correct.
            return CallOriginal(dst, src, bitcount);
        }
    }

    private static bool TryGetRegistration(string serName, string fieldName, int entityIndex, out SpoofEntry reg)
    {
        if (entityIndex >= 0 && _registry.TryGetValue((serName, fieldName, entityIndex), out reg))
        {
            return true;
        }

        return _registry.TryGetValue((serName, fieldName, -1), out reg);
    }

    private static bool InvokeCallback(
        in SpoofEntry reg, nint client, int entityIndex,
        ref int intBits, ref Vector3 vec, ref string? str, ref byte[]? bytes)
    {
        switch (reg.CallbackType)
        {
            case CallbackKind.Int:
                return reg.IntCallback!(client, entityIndex, ref intBits);

            case CallbackKind.Float:
                var f = BitConverter.Int32BitsToSingle(intBits);
                var rf = reg.FloatCallback!(client, entityIndex, ref f);
                intBits = BitConverter.SingleToInt32Bits(f);

                return rf;

            case CallbackKind.Bool:
                var b = intBits != 0;
                var rb = reg.BoolCallback!(client, entityIndex, ref b);
                intBits = b ? 1 : 0;

                return rb;

            case CallbackKind.Vector:
                return reg.VectorCallback!(client, entityIndex, ref vec);

            case CallbackKind.String:
                var s = str ?? string.Empty;
                var rs = reg.StringCallback!(client, entityIndex, ref s);
                str = s;

                return rs;

            case CallbackKind.Bytes:
                var by = bytes ?? Array.Empty<byte>();
                var rby = reg.BytesCallback!(client, entityIndex, ref by);
                bytes = by;

                return rby;

            default:
                return false;
        }
    }

    private static byte VerifyOnly(
        nint dst, nint src, uint bitcount,
        string serName, string fieldName, FieldType fieldType, nint client, int entityIndex)
    {
        var cursorBefore = (dst != 0) ? *(int*) (dst + 0x10) : -1;
        var result       = CallOriginal(dst, src, bitcount);
        var cursorAfter  = (dst != 0) ? *(int*) (dst + 0x10) : -1;

        _logger?.LogInformation(
            "SUBST-VERIFY field=\"{Ser}::{Field}\" type={Type} client=0x{Client:X} ent={Ent} "
            + "bitcount={Bits} cursorBefore={Before} cursorAfter={After}",
            serName, fieldName, fieldType, client, entityIndex, bitcount, cursorBefore, cursorAfter);

        return result;
    }

    private static void LogDiscoveredField(string serName, string fieldName)
    {
        if (_logger is not { } log || _diagCount >= MaxDiagCount || !_diagSeen.TryAdd((serName, fieldName), 0))
        {
            return;
        }

        if (Interlocked.Increment(ref _diagCount) <= MaxDiagCount)
        {
            log.LogInformation("SendProxy field seen: \"{Ser}::{Field}\"", serName, fieldName);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte CallOriginal(nint dst, nint src, uint bitcount)
        => ((delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) _valueCopyTrampoline)(dst, src, bitcount);

    // Walk the CFieldPath (filled by GetBitRange) through the flattened-serializer field array to the leaf
    // record + its field name. Every dereference is IsUserPtr-guarded; a guard failure returns empty (the
    // caller passes through). RE layout: docs/REVERSE_ENGINEERING.md §9b.
    private static string ResolveFieldName(nint serializer, nint hdr, out nint leafRec)
    {
        leafRec = 0;
        if (!NativeUtil.IsUserPtr(serializer) || !NativeUtil.IsUserPtr(hdr))
        {
            return string.Empty;
        }

        // Path levels. CFieldPath is at most 3 deep (§9a); a larger/garbage count means a stale or bogus
        // path — bail rather than walk it.
        var count = *(short*) (hdr + 0x18);
        if (count < 1 || count > 3)
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

        var serArr = serializer;
        var rec    = (nint) 0;

        for (var k = 0; k < count; k++)
        {
            var idxK = *(short*) (idxArr + k * 2);
            if (idxK == 0x7FFF)
            {
                if (k == 0)
                {
                    return string.Empty;
                }

                break;
            }

            // For levels > 0, descend into the child serializer of the current record first.
            if (k > 0)
            {
                var child = *(nint*) (rec + 0x08);
                if (!NativeUtil.IsUserPtr(child))
                {
                    return string.Empty;
                }

                serArr = child;
            }

            // Bound the index against the serializer's field-array length so a stale/garbage index can
            // never deref off the array (the fatal-AV path). The length is the CUtlVector size that
            // precedes the data pointer at +0x30; fall back to a hard cap if it reads implausibly.
            if (!IndexInBounds(serArr, idxK))
            {
                return string.Empty;
            }

            var arr = *(nint*) (serArr + 0x30);
            if (!NativeUtil.IsUserPtr(arr))
            {
                return string.Empty;
            }

            rec = arr + idxK * 0x2E;
            if (!NativeUtil.IsUserPtr(rec))
            {
                return string.Empty;
            }
        }

        var pInfo = *(nint*) (rec + 0x00);
        if (!NativeUtil.IsUserPtr(pInfo))
        {
            return string.Empty;
        }

        leafRec = rec;

        return NativeUtil.ReadShortAscii(*(nint*) (pInfo + 0x08), 48);
    }

    // Largest plausible field-array index — backstop when the array length reads implausibly. A serializer
    // never has anywhere near this many fields, and arr + 4096*0x2E (~0x2C000) stays within the array's
    // mapped region, so a bounded-but-wrong index reads garbage (→ no match → passthrough) instead of
    // faulting on an unmapped page.
    private const int MaxFieldArrayIndex = 4096;

    // True if idx is a valid index into serializer's field array. The CUtlVector element count sits just
    // before the data pointer (count @ serializer+0x28, data @ +0x30); use it when sane, else the cap.
    private static bool IndexInBounds(nint serializer, short idx)
    {
        if (idx < 0)
        {
            return false;
        }

        var count = *(int*) (serializer + 0x28);
        var limit = (count > 0 && count <= MaxFieldArrayIndex) ? count : MaxFieldArrayIndex;

        return idx < limit;
    }

    // Classify the field's encoder family by its live dispatch fn (slot 0 of the dispatch object at
    // fieldInfo+0x38) against the gamedata-resolved encoder map. An unknown fn yields Unsupported, so the
    // caller passes through and never corrupts bits. IsUserPtr-guarded, no try/catch.
    private static FieldType Classify(nint leafRec)
    {
        var map = _encoderTypes;
        if (map is null || !NativeUtil.IsUserPtr(leafRec))
        {
            return FieldType.Unsupported;
        }

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

    // Largest value struct layout the substitute path builds (quant struct ~0x30; byte-array struct needs
    // the count slot at +0x28) fits in 0x30 bytes.
    private const int LiveScratchSize = 0x30;

    // Upper bound (bytes) for a string / byte-array substitute. The substitute buffer is stackalloc'd on
    // the engine send worker thread, so a caller-controlled size must be bounded; oversized values pass
    // the real value through. 4 KiB comfortably covers names and typical small arrays.
    private const int MaxSubstituteBytes = 4096;

    // Data-region array at snapshotPtr+0x30 holds at most 15 entries (EncodeField stack buffer width).
    private const int MaxRegionId = 14;

    // Reconstruct the live entity valuePtr for a quantized-float field from the captured snapshot:
    //   valuePtr = *(nint*)(snapshotPtr + 0x30 + regionId*8) + *(ushort*)(fieldInfo + 0x20)
    //   regionId = *(byte*)(leafRec + 0x2C)
    // IsUserPtr-guarded; returns false on any guard failure.
    private static bool TryGetLiveValuePtr(nint leafRec, out nint valuePtr)
    {
        valuePtr = 0;

        var snapshotPtr = _currentSnapshotPtr;
        if (!NativeUtil.IsUserPtr(snapshotPtr))
        {
            return false;
        }

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

    // Build the fn-pointer -> FieldType map once at Install from the gamedata-resolved bucket handler
    // bases. Each bucket's encoder entries (stride 0x80) carry the encoder-name at +0x00 and the fn at
    // +0x30; the (bucket, name) pair determines the FieldType, exactly as the engine's wiring does. These
    // reads happen once here, off the hot path (Classify is then a dictionary lookup). The single
    // install-time try guards the whole enumeration; a fault yields a partial/empty map (safe passthrough).
    private static Dictionary<nint, FieldType> BuildEncoderTypeMap(ILogger logger)
    {
        var map = new Dictionary<nint, FieldType>();

        if (_bucketAddrs.Length == 0)
        {
            logger.LogWarning("FieldSubstitution: no encoder bucket bases resolved — encoder map empty (all fields Unsupported)");

            return map;
        }

        try
        {
            for (var i = 0; i < _bucketAddrs.Length && i < BucketIndices.Length; i++)
            {
                var bucket  = BucketIndices[i];
                var handler = _bucketAddrs[i];
                var rawCount = NativeUtil.IsUserPtr(_registryAddr) ? *(int*) (_registryAddr + bucket * 16 + 0x08) : -1;
                var name0   = NativeUtil.IsUserPtr(handler) ? NativeUtil.ReadShortAscii(*(nint*) handler, 32) : "<bad>";
                var fn0     = NativeUtil.IsUserPtr(handler) ? *(nint*) (handler + 0x30) : 0;
                logger.LogInformation(
                    "FieldSubstitution diag: bucket={B} handler=0x{H:X} userPtr={U} rawCount={C} entry0.name=\"{N}\" entry0.fn=0x{F:X} (registry=0x{R:X})",
                    bucket, handler, NativeUtil.IsUserPtr(handler), rawCount, name0, fn0, _registryAddr);

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
                    var fn    = *(nint*) (entry + 0x30);
                    if (!NativeUtil.IsUserPtr(fn))
                    {
                        continue;
                    }

                    var name = NativeUtil.ReadShortAscii(*(nint*) (entry + 0x00), 32);
                    var type = ClassifyEntry(bucket, name);
                    if (type == FieldType.Unsupported)
                    {
                        continue;
                    }

                    if (type == FieldType.Int32 && NativeUtil.IsUserPtr(_encodeInt32Addr) && fn != _encodeInt32Addr)
                    {
                        logger.LogWarning(
                            "FieldSubstitution: bucket-1 default fn=0x{Fn:X} != gamedata EncodeInt32 (0x{Sig:X}) — classification may be stale",
                            fn, _encodeInt32Addr);
                    }

                    map[fn] = type;
                    logger.LogInformation("FieldSubstitution encoder: bucket={B} name=\"{Name}\" fn=0x{Fn:X} -> {Type}", bucket, name, fn, type);
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

    private static int ReadBucketCount(int bucket)
        => NativeUtil.IsUserPtr(_registryAddr) ? *(int*) (_registryAddr + bucket * 16 + 0x08) : 0;

    // Map (bucket, encoder-name) -> FieldType, matching the engine's encoder-registry semantics.
    private static FieldType ClassifyEntry(int bucket, string name)
        => bucket switch
        {
            1 => name switch { "default" => FieldType.Int32, "fixed32" => FieldType.Fixed32, "fixed64" => FieldType.Fixed64, _ => FieldType.Unsupported },
            2 => name switch { "default" => FieldType.UInt32, "fixed32" => FieldType.Fixed32, "fixed64" => FieldType.Fixed64, _ => FieldType.Unsupported },
            3 => name switch
            {
                "qangle"         => FieldType.QAngle3,
                "vector3"        => FieldType.Vector3,
                "coord"          => FieldType.Coord3,
                "normal"         => FieldType.Normal3,
                "coord_integral" => FieldType.CoordIntegral3,
                "quantized"      => FieldType.QuantizedFloat,
                _                => FieldType.Unsupported,
            },
            4 => name == "default" ? FieldType.Float32 : FieldType.Unsupported,
            5 => name == "default" ? FieldType.String : FieldType.Unsupported,
            6 => name == "default" ? FieldType.ByteArray : FieldType.Unsupported,
            7 => name == "default" ? FieldType.Bool : FieldType.Unsupported,
            _ => FieldType.Unsupported,
        };

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
