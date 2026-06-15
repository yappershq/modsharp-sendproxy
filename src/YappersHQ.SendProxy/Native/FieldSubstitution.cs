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

// ─────────────────────────────────────────────────────────────────────────────
//  FieldSubstitution — Phase-2 per-client per-field value substitution
// ─────────────────────────────────────────────────────────────────────────────
//
//  WriteFieldList (WFL) runs once per changed-field per client on a send thread.
//  Per-field loop calls:
//    1.  GetBitRange  (FUN_00426260):
//            rdi=CFieldPath* pathOut (WFL stack buffer), rsi=descriptor table, rdx=registry index.
//            We call original first, then capture arg1 into [ThreadStatic] _currentFieldPath.
//    2.  Value-copy  (FUN_00500b70):
//            byte FUN_00500b70(bf_write* dst, bf_read* src, uint bitcount)
//            Copies bitcount bits from the shared pack-buf into WFL's local bf_write.
//            bf_write cursor: *(int*)(dst + 0x10).
//
//  Substitution in the value-copy hook:
//    a)  Save dst cursor.
//    b)  Call original (advances src + dst cursors, writes real bits).
//    c)  Rewind dst cursor.
//    d)  Re-emit the fake value by invoking the FIELD's own engine encoder directly:
//          encoderFn = *(nint*)( *(nint*)(fieldInfo+0x38) )    // slot[0] of the dispatch table
//          ABI (System V): encoder(rdi=bf_write*, rsi=fieldInfo*, rdx=paramsPtr, rcx=valuePtr, r8d=0)
//          paramsPtr: byte off = *(byte*)(fieldInfo+0xC9);
//                     paramsPtr = (off==0xFF) ? 0 : *(nint*)(fieldInfo+0x40) + off;
//          valuePtr: stackalloc scratch holding the fake value in the engine's native layout.
//          Unsupported fields (quantized/coord/normal/string/array/handle) PASSTHROUGH — never substitute.
//
//  Type detection (Classify): the leaf field record's +0x00 is CNetworkSerializerFieldInfo*; the
//  engine dispatches encoding via  *( *(fieldInfo+0x38) + 0x30 )  — fieldInfo+0x38 = type-bucket
//  handler, slot[6] (+0x30) = encode fn. We compare that fn ptr against encoder identities resolved
//  from the registry table. Unknown → Unsupported.
//
//  CFieldPath header (verified by RE of GetBitRange output buffer):
//    hdr+0x00..0x0F : inline short[8] indices (or external short[] ptr when read-only flag != 0)
//    hdr+0x18       : count (short), valid range 1..7
//    hdr+0x1A       : read-only flag (byte)
//    idx[0] == 0x7FFF → empty path.
//
//  Flattened-serializer field array (stride 0x2E, inline):
//    arrayBase = *(nint*)(serializer + 0x30)
//    record_i  = arrayBase + i * 0x2E
//    record+0x00 = CNetworkSerializerFieldInfo* (leaf name at fieldInfo+0x08)
//    record+0x08 = CFlattenedSerializer* child (for descent)
//
//  MODES:
//    Off    — pure passthrough, detours uninstalled.
//    Verify — logs cursor math + field identity, no output change.
//    Fake   — save/call-original/rewind/varint-write.
//
//  THREAD SAFETY:
//    All mutable state is [ThreadStatic] (field path, serializer, entity index) or read-only
//    after Install(). _spoofs and _callbacks are ConcurrentDictionary.
//
// ─────────────────────────────────────────────────────────────────────────────

internal enum SubstitutionMode { Off, Verify, Fake }

// Network encoder family for a registered field, decided by walking the encoder registry table.
//   Int32Signed — bucket 1 "default" (signed int8/16/32/64, zigzag varint).
//   UInt32      — bucket 2 "default" (unsigned int8/16/32/64, raw varint, no zigzag).
//   Fixed32     — bucket 1 or 2 "fixed32" (raw 32-bit write, no varint).
//   Fixed64     — bucket 1 or 2 "fixed64" (raw 64-bit write, no varint).
//   Bool        — bucket 7 "default" (1-bit fixed write).
//   Float32     — bucket 4 "default" (raw 32-bit IEEE-754 inline write).
//   Int64       — alias of Int32Signed (same encoder, value is sign-extended before zigzag).
//   Unsupported — anything else (quantized float/vector, handle, string/bytes, arrays,
//                 bucket 0 no-op stub, unresolved table). MUST passthrough — never substitute.
internal enum FieldType { Unsupported = 0, Int32, UInt32, Int64, Bool, Float32, Fixed32, Fixed64 }

internal static unsafe class FieldSubstitution
{
    // ── Mode ─────────────────────────────────────────────────────────────────

    private static volatile int _mode = (int) SubstitutionMode.Off;
    public static SubstitutionMode Mode
    {
        get => (SubstitutionMode) _mode;
        set => Interlocked.Exchange(ref _mode, (int) value);
    }

    // ── Unified registration record ──────────────────────────────────────────
    //
    //  A single registration holds an optional uniform spoof value and/or a per-client callback.
    //  entityIndex in the dict key: -1 = all entities (global scope), >= 0 = specific entity.
    //
    //  Lookup in ValueCopyHook is a two-step probe:
    //    1. (ser, field, _currentEntityIndex)  — entity-specific wins.
    //    2. (ser, field, -1)                   — global fallback.
    //  Both steps are plain ConcurrentDictionary.TryGetValue — no lock on hot path.

    private readonly struct SpoofEntry
    {
        public readonly bool             HasSpoof;
        public readonly int              SpoofValue;
        public readonly PerClientIntProxy? Callback;

        public SpoofEntry(int spoofValue) : this()
        {
            HasSpoof   = true;
            SpoofValue = spoofValue;
        }

        public SpoofEntry(PerClientIntProxy callback) : this()
        {
            Callback = callback;
        }

        public SpoofEntry(int spoofValue, PerClientIntProxy? callback) : this()
        {
            HasSpoof   = true;
            SpoofValue = spoofValue;
            Callback   = callback;
        }
    }

    // Key: (ser, field, entityIndex) — entityIndex -1 = all entities.
    private static readonly ConcurrentDictionary<(string ser, string field, int entityIndex), SpoofEntry> _registry = new();

    // ── Global (all-entity) spoof / callback helpers ─────────────────────────

    public static void SetSpoof(string serializerName, string fieldName, int value)
        => _registry[(serializerName, fieldName, -1)] = new SpoofEntry(value);

    public static void SetCallback(string serializerName, string fieldName, PerClientIntProxy callback)
        => _registry[(serializerName, fieldName, -1)] = new SpoofEntry(callback);

    public static void ClearCallback(string serializerName, string fieldName)
        => _registry.TryRemove((serializerName, fieldName, -1), out _);

    public static void ClearCallbacks()
    {
        // Remove only global (-1) callback entries; leave entity-specific spoofs.
        foreach (var key in _registry.Keys)
        {
            if (key.entityIndex == -1 && _registry.TryGetValue(key, out var e) && e.Callback is not null)
                _registry.TryRemove(key, out _);
        }
    }

    public static void ClearSpoofs()
    {
        foreach (var key in _registry.Keys)
        {
            if (key.entityIndex == -1 && _registry.TryGetValue(key, out var e) && e.HasSpoof && e.Callback is null)
                _registry.TryRemove(key, out _);
        }
    }

    public static bool HasSpoofs    => !_registry.IsEmpty && HasEntriesMatching(static e => e.HasSpoof && e.Callback is null);
    public static bool HasCallbacks => !_registry.IsEmpty && HasEntriesMatching(static e => e.Callback is not null);

    private static bool HasEntriesMatching(Func<SpoofEntry, bool> pred)
    {
        foreach (var kv in _registry)
            if (pred(kv.Value)) return true;
        return false;
    }

    // ── Per-entity spoof / callback helpers ──────────────────────────────────

    /// <summary>Register a uniform-value spoof for a specific entity index only.</summary>
    public static void SetEntitySpoof(int entityIndex, string serializerName, string fieldName, int value)
        => _registry[(serializerName, fieldName, entityIndex)] = new SpoofEntry(value);

    /// <summary>Register a per-client callback scoped to a specific entity index only.</summary>
    public static void SetEntityCallback(int entityIndex, string serializerName, string fieldName, PerClientIntProxy callback)
        => _registry[(serializerName, fieldName, entityIndex)] = new SpoofEntry(callback);

    /// <summary>Remove the entity-specific registration for (entityIndex, ser, field). Does not touch the global -1 entry.</summary>
    public static void ClearEntityRegistration(int entityIndex, string serializerName, string fieldName)
        => _registry.TryRemove((serializerName, fieldName, entityIndex), out _);

    /// <summary>Remove ALL registrations (global + entity-specific). Called from UnhookAllPerClient before Uninstall.</summary>
    public static void ClearAll() => _registry.Clear();

    // ── [ThreadStatic] per-call context ─────────────────────────────────────

    // Filled in GetBitRange detour after the original runs (pathOut is valid for the following value-copy call).
    [ThreadStatic] private static nint _currentFieldPath;   // CFieldPath* (arg1 of GetBitRange, post-fill)

    // Filled in WFL shim — WFL param_1 (CFlattenedSerializer* in rdi).
    [ThreadStatic] private static nint _currentSerializer;

    // Filled in WDE entity-index capture — *(int*)(ctx+0x34). -1 if not captured.
    [ThreadStatic] private static int _currentEntityIndex;

    // Diagnostic: log first N distinct (ser, leaf) pairs seen.
    private static int _diagCount;
    private const  int MaxDiagCount = 25;
    private static readonly ConcurrentDictionary<(string, string), byte> _diagSeen = new();

    // Log throttle for Verify/Fake first-N messages.
    private static int _logCount;
    private const  int MaxLogCount = 20;

    // ── Hooks ────────────────────────────────────────────────────────────────

    private static IDetourHook? _getBitRangeHook;
    private static nint         _getBitRangeTrampoline;
    private static IDetourHook? _valueCopyHook;
    private static nint         _valueCopyTrampoline;
    private static IDetourHook? _wflShimHook;
    private static nint         _wflShimTrampoline;
    private static IDetourHook? _wdeEntityCaptureHook;
    private static nint         _wdeEntityCaptureTrampoline;

    private static ILogger? _logger;

    // ── Addresses (set by SendProxyModule before Install) ────────────────────

    public static nint GetBitRangeAddr;    // file-vaddr 0x326260
    public static nint ValueCopyAddr;      // file-vaddr 0x400b70
    public static nint WriteFieldListAddr; // file-vaddr 0x343b60
    public static nint WdeAddr;            // WriteDeltaEntity_Internal (0 = skip entity-index capture)

    // ── Encoder registry table (set by SendProxyModule before Install) ───────
    //  Absolute address of the 8-bucket encoder-registry table in networksystem's .data section
    //  (file-vaddr 0x45f360, resolved via "CFlattenedSerializer::EncoderRegistry" gamedata entry).
    //  Set to 0 if gamedata resolution failed — Classify returns Unsupported for every field (safe).
    public static nint RegistryAddr;

    // ── Lazy fn→FieldType map (built once from the registry table on first Install) ──────────
    //  Key   = encode fn pointer read from entry+0x30 inside each registry bucket.
    //  Value = FieldType classification per bucket index and entry name.
    //  Built under _encoderTypesLock; after that, read-only (concurrent reads safe without lock).
    private static Dictionary<nint, FieldType>? _encoderTypes;
    private static readonly object _encoderTypesLock = new();

    // ── Install / Uninstall ──────────────────────────────────────────────────

    public static bool Install(InterfaceBridge bridge, ILogger logger)
    {
        _logger = logger;

        if (!ValidateAddresses(logger))
            return false;

        // Build fn→FieldType map from registry table if not yet done.
        // Thread-safe: double-checked lock; map is read-only after this point.
        if (_encoderTypes is null)
        {
            lock (_encoderTypesLock)
            {
                if (_encoderTypes is null)
                    _encoderTypes = BuildEncoderTypeMap(logger);
            }
        }

        // 1. WFL shim — captures serializer ptr (rdi) into _currentSerializer [ThreadStatic].
        if (_wflShimHook is null)
        {
            var wflHook   = bridge.HookManager.CreateDetourHook();
            var wflHookFn = (nint) (delegate* unmanaged[Cdecl]<
                nint, nint, nint, nint, nint, uint,
                uint, nint, uint,
                nint>) &WflShim;
            wflHook.Prepare(WriteFieldListAddr, wflHookFn);
            if (!wflHook.Install())
            {
                logger.LogWarning("FieldSubstitution: WFL shim Install() failed");
                return false;
            }
            _wflShimHook       = wflHook;
            _wflShimTrampoline = wflHook.Trampoline;
            logger.LogInformation("FieldSubstitution: WFL shim installed @ 0x{Addr:X}", WriteFieldListAddr);
        }

        // 2. GetBitRange hook — calls original first, then captures arg1 (filled pathOut).
        if (_getBitRangeHook is null)
        {
            var gbrHook   = bridge.HookManager.CreateDetourHook();
            var gbrHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, uint, void>) &GetBitRangeHook;
            gbrHook.Prepare(GetBitRangeAddr, gbrHookFn);
            if (!gbrHook.Install())
            {
                logger.LogWarning("FieldSubstitution: GetBitRange hook Install() failed");
                _wflShimHook?.Uninstall(); _wflShimHook?.Dispose(); _wflShimHook = null;
                return false;
            }
            _getBitRangeHook       = gbrHook;
            _getBitRangeTrampoline = gbrHook.Trampoline;
            logger.LogInformation("FieldSubstitution: GetBitRange hook installed @ 0x{Addr:X}", GetBitRangeAddr);
        }

        // 3. Value-copy hook — performs substitution.
        if (_valueCopyHook is null)
        {
            var vcHook   = bridge.HookManager.CreateDetourHook();
            var vcHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) &ValueCopyHook;
            vcHook.Prepare(ValueCopyAddr, vcHookFn);
            if (!vcHook.Install())
            {
                logger.LogWarning("FieldSubstitution: value-copy hook Install() failed");
                _getBitRangeHook?.Uninstall(); _getBitRangeHook?.Dispose(); _getBitRangeHook = null;
                _wflShimHook?.Uninstall(); _wflShimHook?.Dispose(); _wflShimHook = null;
                return false;
            }
            _valueCopyHook       = vcHook;
            _valueCopyTrampoline = vcHook.Trampoline;
            logger.LogInformation("FieldSubstitution: value-copy hook installed @ 0x{Addr:X}", ValueCopyAddr);
        }

        // 4. WDE entity-index capture — optional; entityIndex will be -1 in callbacks if skipped.
        //    ABI: rdi=this, rsi=ctx. *(int*)(ctx+0x34) = entityIndex.
        if (_wdeEntityCaptureHook is null && WdeAddr != 0)
        {
            var wdeHook   = bridge.HookManager.CreateDetourHook();
            var wdeHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) &WdeEntityCaptureHook;
            wdeHook.Prepare(WdeAddr, wdeHookFn);
            if (wdeHook.Install())
            {
                _wdeEntityCaptureHook       = wdeHook;
                _wdeEntityCaptureTrampoline = wdeHook.Trampoline;
                logger.LogInformation("FieldSubstitution: WDE entity-index capture installed @ 0x{Addr:X}", WdeAddr);
            }
            else
            {
                // Non-fatal — per-client callbacks still work with entityIndex == -1.
                logger.LogWarning("FieldSubstitution: WDE entity-index capture Install() failed — entityIndex will be -1 in callbacks");
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

        _valueCopyHook?.Uninstall();        _valueCopyHook?.Dispose();        _valueCopyHook            = null; _valueCopyTrampoline            = 0;
        _getBitRangeHook?.Uninstall();      _getBitRangeHook?.Dispose();      _getBitRangeHook          = null; _getBitRangeTrampoline          = 0;
        _wflShimHook?.Uninstall();          _wflShimHook?.Dispose();          _wflShimHook              = null; _wflShimTrampoline              = 0;
        _wdeEntityCaptureHook?.Uninstall(); _wdeEntityCaptureHook?.Dispose(); _wdeEntityCaptureHook     = null; _wdeEntityCaptureTrampoline     = 0;

        _logger?.LogInformation("FieldSubstitution: all hooks uninstalled");
        _logger = null;
    }

    // ── WFL shim ─────────────────────────────────────────────────────────────
    //  Captures WFL rdi (CFlattenedSerializer*) into _currentSerializer, passes all 9 args through.

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WflShim(
        nint a,   // rdi — CFlattenedSerializer*
        nint b,   nint c, nint d, nint e,
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

    // ── WDE entity-index capture hook ────────────────────────────────────────
    //  ABI: rdi=a (CNetworkGameServerBase*), rsi=b (delta ctx*). *(int*)(b+0x34) = entityIndex.
    //  Reads entity index before calling trampoline; restores -1 in finally (no stale leaks).

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WdeEntityCaptureHook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        var entityIndex = -1;
        if (NativeUtil.IsUserPtr(b))
        {
            try { entityIndex = *(int*) (b + 0x34); }
            catch { }
        }

        _currentEntityIndex = entityIndex;
        nint result;
        try
        {
            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)
                _wdeEntityCaptureTrampoline)(a, b, c, d, e, f);
        }
        finally
        {
            _currentEntityIndex = -1;
        }
        return result;
    }

    // ── GetBitRange hook ─────────────────────────────────────────────────────
    //  ABI: FUN_00426260(pathOut /*rdi*/, table /*rsi*/, registryIndex /*rdx*/)
    //  Call original first (fills pathOut), then capture arg1.

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GetBitRangeHook(nint pathOut, nint table, uint registryIndex)
    {
        ((delegate* unmanaged[Cdecl]<nint, nint, uint, void>) _getBitRangeTrampoline)(pathOut, table, registryIndex);
        _currentFieldPath = pathOut;
    }

    // ── Value-copy hook ───────────────────────────────────────────────────────
    //  ABI: byte FUN_00500b70(bf_write* dst, bf_read* src, uint bitcount)
    //  bf_write cursor: *(int*)(dst + 0x10).

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte ValueCopyHook(nint dst, nint src, uint bitcount)
    {
        var mode = (SubstitutionMode) _mode;
        if (mode == SubstitutionMode.Off)
            goto Passthrough;

        var serPtr = _currentSerializer;
        if (!NativeUtil.IsUserPtr(serPtr))
            goto Passthrough;

        try
        {
            var serName   = NativeUtil.ReadShortAscii(*(nint*) (serPtr + 0x00), 48);
            var fieldName = ResolveFieldName(serPtr, _currentFieldPath, out var leafRec);

            // Diagnostic: log first MaxDiagCount distinct (ser, leaf) pairs.
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
                            var count = (hdr != 0) ? *(short*)(hdr + 0x18) : (short)0;
                            var i0    = (count > 0) ? *(short*)(hdr + 0x00) : (short)-1;
                            var i1    = (count > 1) ? *(short*)(hdr + 0x02) : (short)-1;
                            var i2    = (count > 2) ? *(short*)(hdr + 0x04) : (short)-1;
                            diagLog.LogInformation(
                                "WFLD#{N} ser=\"{Ser}\" count={Count} idx=[{I0},{I1},{I2}] name=\"{Name}\"",
                                n, serName, count, i0, i1, i2, fieldName);
                        }
                        catch { }
                    }
                }
            }

            if (fieldName.Length == 0)
                goto Passthrough;

            var client      = RecipientCapture.CurrentClient;
            var entityIndex = _currentEntityIndex;

            // Two-step probe: entity-specific entry wins over the global (-1) fallback.
            // Both lookups are lock-free ConcurrentDictionary.TryGetValue.
            SpoofEntry reg;
            bool hasReg;
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
                goto Passthrough;

            var fieldType = Classify(leafRec);

            var ln = Interlocked.Increment(ref _logCount);

            // Diagnostic: log first MaxLogCount substitution matches with entity-targeting info.
            if (ln <= MaxLogCount && _logger is { } diagSubstLog)
            {
                try
                {
                    var regEntIdx = _registry.TryGetValue((serName, fieldName, entityIndex), out _) && entityIndex >= 0
                        ? entityIndex : -1;
                    diagSubstLog.LogInformation(
                        "SUBST ent={CurEnt} ser=\"{Ser}\" field=\"{Field}\" client=0x{Client:X} target={RegEnt}",
                        entityIndex, serName, fieldName, client, regEntIdx);
                }
                catch { }
            }

            if (mode == SubstitutionMode.Verify)
            {
                int cursorBefore = (dst != 0) ? *(int*) (dst + 0x10) : -1;
                byte result = CallOriginal(dst, src, bitcount);
                int cursorAfter  = (dst != 0) ? *(int*) (dst + 0x10) : -1;

                if (ln <= MaxLogCount && _logger is { } log)
                {
                    log.LogInformation(
                        "SUBST-VERIFY field=\"{Ser}::{Field}\" type={Type} client=0x{Client:X} ent={Ent} bitcount={Bits} "
                        + "cursorBefore={Before} cursorAfter={After} (fake would be {Fake})",
                        serName, fieldName, fieldType, client, entityIndex, bitcount, cursorBefore, cursorAfter, reg.SpoofValue);
                }
                return result;
            }
            else  // Fake
            {
                // Determine effective fake value:
                //   1. Start with the registered spoof value (0 if HasSpoof is false).
                //   2. Invoke per-client callback if present — may overwrite value, returns true → substitute, false → passthrough.
                // A throwing callback is caught → passthrough for this field/client.

                int  effectiveFake   = reg.HasSpoof ? reg.SpoofValue : 0;
                bool shouldSubstitute = reg.Callback is null; // no callback → uniform spoof, always substitute

                if (reg.Callback is not null)
                {
                    try
                    {
                        shouldSubstitute = reg.Callback(client, entityIndex, ref effectiveFake);
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
                            catch { }
                        }
                        return CallOriginal(dst, src, bitcount);
                    }
                }

                if (!shouldSubstitute)
                    return CallOriginal(dst, src, bitcount);

                // DEFAULT-SAFE: only substitute field types we know how to encode. An unrecognised
                // (or unresolved) encoder MUST pass through untouched — writing wrong-type bits would
                // corrupt the entity delta for every client.
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

                // Save cursor, call original (advances src + dst cursors + writes real bits), then
                // rewind the dst cursor and re-emit our value by calling the field's own engine encoder.
                int savedCursor     = *(int*) (dst + 0x10);
                byte originalResult = CallOriginal(dst, src, bitcount);
                int afterOriginal   = *(int*) (dst + 0x10);

                *(int*) (dst + 0x10) = savedCursor;

                // Resolve the field's per-instance encoder fn: slot[0] of the dispatch table at
                // fieldInfo+0x38 (NOT slot[6]/+0x30, which is the registry handler used only for Classify).
                // ABI (System V x64):
                //   encoder(rdi=bf_write*, rsi=fieldInfo*, rdx=paramsPtr, rcx=valuePtr, r8d=0)
                // paramsPtr derivation replicates the engine call site:
                //   byte off = *(byte*)(fieldInfo+0xC9); paramsPtr = (off==0xFF)?0:*(nint*)(fieldInfo+0x40)+off
                // valuePtr points to a 16-byte scratch containing the fake value in its native engine layout.
                var fieldInfo  = *(nint*) (leafRec + 0x00);
                var dispatch   = *(nint*) (fieldInfo + 0x38);
                var encoderFn  = NativeUtil.IsUserPtr(dispatch) ? *(nint*) dispatch : 0;
                byte paramOff  = *(byte*) (fieldInfo + 0xC9);
                var paramsPtr  = (paramOff == 0xFF) ? 0 : (*(nint*) (fieldInfo + 0x40) + paramOff);

                if (!NativeUtil.IsUserPtr(encoderFn))
                {
                    // Encoder fn unresolvable — fall back to the engine's already-written bits.
                    *(int*) (dst + 0x10) = afterOriginal;
                    return originalResult;
                }

                // Build a 16-byte scratch with the fake value in the engine's native in-memory layout.
                // Layout per FieldType (matches what the engine's in-process field stores):
                //   Int32/Int64/Fixed32/Fixed64 (signed): *(long*) = sign-extended effectiveFake
                //   UInt32/UInt64:                        *(ulong*)= (uint)effectiveFake  (zero-high)
                //   Bool:                                 *(byte*) = 0 or 1
                //   Float32: encoder reads *(double*) and narrows; supply sign-extended IEEE bits
                var scratch = stackalloc byte[16];
                switch (fieldType)
                {
                    case FieldType.Int32:
                    case FieldType.Int64:
                    case FieldType.Fixed32:
                    case FieldType.Fixed64:
                        *(long*) scratch = (long) effectiveFake;    // sign-extend int → long
                        break;
                    case FieldType.UInt32:
                        *(ulong*) scratch = (uint) effectiveFake;   // zero-extend to 64 bits
                        break;
                    case FieldType.Bool:
                        *scratch = (byte) (effectiveFake != 0 ? 1 : 0);
                        break;
                    case FieldType.Float32:
                        // Encoder reads *(double*) then narrows to float.
                        // effectiveFake carries the IEEE-754 bit pattern (caller used SingleToInt32Bits).
                        *(double*) scratch = (double) BitConverter.Int32BitsToSingle(effectiveFake);
                        break;
                    default:
                        // Unreachable — Unsupported was gated above — but be defensive.
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
                    // Encoder faulted — restore post-original cursor so engine bits stand.
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Resolve the leaf field name from a CFlattenedSerializer and the CFieldPath buffer filled by GetBitRange.
    ///
    ///     CFieldPath header (hdr): inline short[8] at hdr+0x00 (or external short[] when hdr+0x1A != 0),
    ///     count at hdr+0x18, sentinel idx[0]==0x7FFF means empty path.
    ///
    ///     Flattened-serializer field array (stride 0x2E, inline at serializer+0x30):
    ///       record+0x00 = CNetworkSerializerFieldInfo* (leaf name at fieldInfo+0x08)
    ///       record+0x08 = CFlattenedSerializer* child (for multi-level descent)
    ///
    ///     All dereferences are NativeUtil.IsUserPtr-gated; function is try/catch-wrapped → "" on fault.
    /// </summary>
    private static string ResolveFieldName(nint serializer, nint hdr)
        => ResolveFieldName(serializer, hdr, out _);

    /// <param name="leafRec">
    ///     On success, the resolved leaf field record (stride-0x2E entry whose +0x00 is the
    ///     CNetworkSerializerFieldInfo*). 0 on any fault. Used by <see cref="Classify"/>.
    /// </param>
    private static string ResolveFieldName(nint serializer, nint hdr, out nint leafRec)
    {
        leafRec = 0;
        if (!NativeUtil.IsUserPtr(serializer) || !NativeUtil.IsUserPtr(hdr))
            return string.Empty;

        try
        {
            var count = *(short*) (hdr + 0x18);
            if (count <= 0 || count > 7)
                return string.Empty;

            nint idxArr;
            if (*(byte*) (hdr + 0x1A) != 0)
            {
                // Read-only: hdr+0x00 holds a pointer to an external short[].
                idxArr = *(nint*) hdr;
                if (!NativeUtil.IsUserPtr(idxArr))
                    return string.Empty;
            }
            else
            {
                idxArr = hdr;
            }

            var idx0 = *(short*) (idxArr + 0 * 2);
            if (idx0 == 0x7FFF)
                return string.Empty;

            var arr0 = *(nint*) (serializer + 0x30);
            if (!NativeUtil.IsUserPtr(arr0))
                return string.Empty;

            var rec = arr0 + idx0 * 0x2E;

            for (var k = 1; k < count; k++)
            {
                var idxK = *(short*) (idxArr + k * 2);
                if (idxK == 0x7FFF) break;

                var child = *(nint*) (rec + 0x08);
                if (!NativeUtil.IsUserPtr(child))
                    return string.Empty;

                var arrK = *(nint*) (child + 0x30);
                if (!NativeUtil.IsUserPtr(arrK))
                    return string.Empty;

                rec = arrK + idxK * 0x2E;
            }

            var pInfo = *(nint*) (rec + 0x00);
            if (!NativeUtil.IsUserPtr(pInfo))
                return string.Empty;

            leafRec = rec;
            return NativeUtil.ReadShortAscii(*(nint*) (pInfo + 0x08), 48);
        }
        catch
        {
            leafRec = 0;
            return string.Empty;
        }
    }

    /// <summary>
    ///     Classify a field's network encoder family from its leaf record.
    ///
    ///     The leaf record's +0x00 is the CNetworkSerializerFieldInfo*. The engine dispatches
    ///     encoding through  encoderFn = *( *(fieldInfo+0x38) + 0x30 )  — fieldInfo+0x38 is the
    ///     type-bucket handler object, slot[6] (+0x30) is its encode fn. We look that fn up in
    ///     the _encoderTypes map built from the registry table by BuildEncoderTypeMap.
    ///
    ///     Any fault or fn not found in the map yields Unsupported → caller passes through
    ///     (never corrupts bits).
    /// </summary>
    private static FieldType Classify(nint leafRec)
    {
        if (!NativeUtil.IsUserPtr(leafRec))
            return FieldType.Unsupported;

        var map = _encoderTypes;
        if (map is null)
            return FieldType.Unsupported;

        try
        {
            var fieldInfo = *(nint*) (leafRec + 0x00);
            if (!NativeUtil.IsUserPtr(fieldInfo))
                return FieldType.Unsupported;

            var handler = *(nint*) (fieldInfo + 0x38);
            if (!NativeUtil.IsUserPtr(handler))
                return FieldType.Unsupported;

            var encoderFn = *(nint*) (handler + 0x30);
            if (!NativeUtil.IsUserPtr(encoderFn))
                return FieldType.Unsupported;

            return map.TryGetValue(encoderFn, out var t) ? t : FieldType.Unsupported;
        }
        catch
        {
            return FieldType.Unsupported;
        }
    }

    /// <summary>
    ///     Walk the 8-bucket encoder-registry table at <see cref="RegistryAddr"/> and build a
    ///     fn-pointer → FieldType dictionary.
    ///
    ///     Registry layout (file-vaddr 0x45f360):
    ///       8 × 16-byte buckets: { nint handler (+0x00), int count (+0x08) }
    ///       handler → array of `count` entries, stride 0x80:
    ///         entry+0x00 = char* name
    ///         entry+0x30 = encode fn ptr
    ///
    ///     Bucket semantics (b-index → classification):
    ///       b0  default = no-op stub                     → Unsupported
    ///       b1  default = signed zigzag varint           → Int32Signed (Int32)
    ///           fixed32 = raw 32-bit write               → Fixed32
    ///           fixed64 = raw 64-bit write               → Fixed64
    ///       b2  default = unsigned raw varint            → UInt32
    ///           fixed32 = raw 32-bit write               → Fixed32
    ///           fixed64 = raw 64-bit write               → Fixed64
    ///       b3  all     = quantized float/vector/angle   → Unsupported (multi-component)
    ///       b4  default = raw 32-bit IEEE-754            → Float32
    ///       b5  all     = uint64/handle/string           → Unsupported
    ///       b6  all     = array                          → Unsupported
    ///       b7  default = 1-bit bool                     → Bool
    ///
    ///     Invalid/non-user-ptr fn ptrs and Unsupported types are silently skipped.
    ///     Returns an empty (but non-null) dictionary on any global fault.
    /// </summary>
    private static Dictionary<nint, FieldType> BuildEncoderTypeMap(ILogger logger)
    {
        var map = new Dictionary<nint, FieldType>();

        if (RegistryAddr == 0)
        {
            logger.LogWarning("FieldSubstitution: registry address is 0 — encoder type map will be empty (all fields Unsupported)");
            return map;
        }

        try
        {
            for (var b = 0; b < 8; b++)
            {
                var bucketBase = RegistryAddr + b * 16;
                var handler    = *(nint*) (bucketBase + 0x00);
                var count      = *(int*)  (bucketBase + 0x08);

                if (!NativeUtil.IsUserPtr(handler) || count <= 0 || count > 32)
                    continue;

                for (var e = 0; e < count; e++)
                {
                    var entry = handler + e * 0x80;

                    nint namePtr, fn;
                    try
                    {
                        namePtr = *(nint*) (entry + 0x00);
                        fn      = *(nint*) (entry + 0x30);
                    }
                    catch { continue; }

                    if (!NativeUtil.IsUserPtr(fn))
                        continue;

                    var name = NativeUtil.ReadShortAscii(namePtr, 32);
                    var type = ClassifyByBucketAndName(b, name);

                    if (type == FieldType.Unsupported)
                        continue;

                    map[fn] = type;
                    logger.LogInformation(
                        "FieldSubstitution registry: bucket={B} name=\"{Name}\" fn=0x{Fn:X} → {Type}",
                        b, name, fn, type);
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

    /// <summary>
    ///     Determine FieldType from (bucket index, encoder entry name).
    ///     Returns Unsupported for any bucket/name combination we don't substitute.
    /// </summary>
    private static FieldType ClassifyByBucketAndName(int bucket, string name)
    {
        switch (bucket)
        {
            case 1: // signed int bucket
                if (name == "default") return FieldType.Int32;
                if (name == "fixed32") return FieldType.Fixed32;
                if (name == "fixed64") return FieldType.Fixed64;
                return FieldType.Unsupported;

            case 2: // unsigned int bucket
                if (name == "default") return FieldType.UInt32;
                if (name == "fixed32") return FieldType.Fixed32;
                if (name == "fixed64") return FieldType.Fixed64;
                return FieldType.Unsupported;

            case 4: // float32 bucket
                if (name == "default") return FieldType.Float32;
                return FieldType.Unsupported;

            case 7: // bool bucket
                if (name == "default") return FieldType.Bool;
                return FieldType.Unsupported;

            default:
                // b0 (no-op), b3 (quantized float/vector/angle), b5 (uint64/handle/string),
                // b6 (array): none are substitutable.
                return FieldType.Unsupported;
        }
    }

    private static bool ValidateAddresses(ILogger logger)
    {
        var ok = true;
        if (GetBitRangeAddr    == 0) { logger.LogWarning("FieldSubstitution: GetBitRange address not resolved");    ok = false; }
        if (ValueCopyAddr      == 0) { logger.LogWarning("FieldSubstitution: ValueCopy address not resolved");      ok = false; }
        if (WriteFieldListAddr == 0) { logger.LogWarning("FieldSubstitution: WriteFieldList address not resolved"); ok = false; }
        return ok;
    }
}
