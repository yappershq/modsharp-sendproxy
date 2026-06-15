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
//    d)  Re-emit the fake value using the FIELD's real encoder family (see Classify):
//          - Int32/Int64 — signed zigzag varint via FUN_00500890 (rdi=dst, rsi=zigzag-of-int64).
//          - UInt32      — raw varint via the same FUN_00500890 (no zigzag).
//          - Bool        — 1-bit fixed write (WriteUBitLong).
//          - Float32     — 32-bit fixed write of the IEEE-754 bits (WriteUBitLong).
//          - Unsupported — PASSTHROUGH (re-call would corrupt; we leave the engine's bits).
//        FUN_00500890 is the engine's single varint primitive: void(bf_write*, uint64). It handles
//        both signed (caller pre-zigzags) and unsigned, 32- and 64-bit, in one place.
//
//  Type detection (Classify): the leaf field record's +0x00 is CNetworkSerializerFieldInfo*; the
//  engine dispatches encoding via  *( *(fieldInfo+0x38) + 0x30 )  — fieldInfo+0x38 = type-bucket
//  handler, slot[6] (+0x30) = encode fn. We compare that fn ptr against encoder identities resolved
//  from gamedata (EncodeInt32/EncodeUInt32/EncodeFloat32/EncodeBool). Unknown → Unsupported.
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

    // ── Spoof registry ───────────────────────────────────────────────────────

    // Key: (serializerName, fieldName) → fake int32 value.
    private static readonly ConcurrentDictionary<(string ser, string field), int> _spoofs = new();

    public static void SetSpoof(string serializerName, string fieldName, int value)
        => _spoofs[(serializerName, fieldName)] = value;

    public static void ClearSpoofs() => _spoofs.Clear();
    public static bool HasSpoofs    => !_spoofs.IsEmpty;

    // ── Per-client callback registry ─────────────────────────────────────────
    //
    //  Key: (serializerName, fieldName) → PerClientIntProxy.
    //  Callback receives: client (CServerSideClient*, 0 if not captured), entityIndex (-1 if unknown),
    //  value (ref int, pre-seeded with uniform spoof or 0). Returns true → substitute; false → passthrough.
    //  MUST be thread-safe and non-blocking. Throwing callbacks are caught → passthrough.

    private static readonly ConcurrentDictionary<(string ser, string field), PerClientIntProxy> _callbacks = new();

    public static void SetCallback(string serializerName, string fieldName, PerClientIntProxy callback)
        => _callbacks[(serializerName, fieldName)] = callback;

    public static void ClearCallback(string serializerName, string fieldName)
        => _callbacks.TryRemove((serializerName, fieldName), out _);

    public static void ClearCallbacks() => _callbacks.Clear();
    public static bool HasCallbacks     => !_callbacks.IsEmpty;

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

    // FUN_00500890 — varint writer: void(bf_write* dst, uint64 value). The engine uses ONE varint
    // primitive for signed (caller pre-zigzags) and unsigned, 32- and 64-bit (rsi is 64-bit wide).
    // We type the value as ulong so Int64/UInt32/UInt64 all route through it correctly.
    private static delegate* unmanaged[Cdecl]<nint, ulong, void> _varintWriter;
    private static ILogger? _logger;

    // ── Addresses (set by SendProxyModule before Install) ────────────────────

    public static nint GetBitRangeAddr;    // file-vaddr 0x326260
    public static nint ValueCopyAddr;      // file-vaddr 0x400b70
    public static nint VarintWriterAddr;   // file-vaddr 0x400890
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

        _varintWriter = (delegate* unmanaged[Cdecl]<nint, ulong, void>) VarintWriterAddr;

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

            _spoofs.TryGetValue((serName, fieldName), out var uniformFakeValue);
            _callbacks.TryGetValue((serName, fieldName), out var clientCallback);

            if (clientCallback is null && !_spoofs.ContainsKey((serName, fieldName)))
                goto Passthrough;

            var client      = RecipientCapture.CurrentClient;
            var entityIndex = _currentEntityIndex;
            var fieldType   = Classify(leafRec);

            var ln = Interlocked.Increment(ref _logCount);

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
                        serName, fieldName, fieldType, client, entityIndex, bitcount, cursorBefore, cursorAfter, uniformFakeValue);
                }
                return result;
            }
            else  // Fake
            {
                // Determine effective fake value:
                //   1. Start with the uniform spoof (0 if none).
                //   2. Invoke per-client callback — may overwrite value and return true (substitute) or false (passthrough).
                // A throwing callback is caught → passthrough for this field/client.

                int effectiveFake    = uniformFakeValue;
                bool shouldSubstitute = clientCallback is null;

                if (clientCallback is not null)
                {
                    try
                    {
                        shouldSubstitute = clientCallback(client, entityIndex, ref effectiveFake);
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
                // rewind the dst cursor and re-emit our value with the field's real encoder.
                int savedCursor     = *(int*) (dst + 0x10);
                byte originalResult = CallOriginal(dst, src, bitcount);
                int afterOriginal   = *(int*) (dst + 0x10);

                *(int*) (dst + 0x10) = savedCursor;

                switch (fieldType)
                {
                    case FieldType.Int32:
                    case FieldType.Int64:
                    {
                        // Signed zigzag varint. Zigzag over the sign-extended 64-bit value so the
                        // byte stream matches the engine for both int32 and int64 widths.
                        long  v64    = effectiveFake;                       // sign-extend int → long
                        ulong zigzag = (ulong) ((v64 << 1) ^ (v64 >> 63));
                        _varintWriter(dst, zigzag);
                        break;
                    }
                    case FieldType.UInt32:
                    {
                        // Raw varint, no zigzag. Mask to 32 bits (callback value is int-shaped).
                        _varintWriter(dst, (uint) effectiveFake);
                        break;
                    }
                    case FieldType.Fixed32:
                    {
                        // Raw 32-bit fixed-width write (no varint, no zigzag).
                        // Used by signed/unsigned integer fields tagged with "fixed32" in the registry.
                        // effectiveFake carries the 32-bit pattern (callers may pass via BitConverter).
                        WriteUBitLong(dst, (uint) effectiveFake, 32);
                        break;
                    }
                    case FieldType.Fixed64:
                    {
                        // Raw 64-bit fixed-width write: two consecutive 32-bit little-endian words.
                        // The int API means high 32 bits come from sign-extension of effectiveFake.
                        // For typical use (substitute a 32-bit-range value) high word is 0 or ~0.
                        var u = (ulong) (long) effectiveFake; // sign-extend int → long → ulong
                        WriteUBitLong(dst, (uint)  u,         32); // low  word
                        WriteUBitLong(dst, (uint) (u >> 32),  32); // high word
                        break;
                    }
                    case FieldType.Bool:
                    {
                        WriteUBitLong(dst, effectiveFake != 0 ? 1u : 0u, 1);
                        break;
                    }
                    case FieldType.Float32:
                    {
                        // Reinterpret the int payload's bits as the 32-bit IEEE-754 value to emit.
                        // Callers pass the float via BitConverter.SingleToInt32Bits.
                        WriteUBitLong(dst, (uint) effectiveFake, 32);
                        break;
                    }
                    default:
                    {
                        // Unreachable (Unsupported handled above) — but be defensive: keep the
                        // engine's own (already-written) output by restoring the post-original
                        // cursor. Do NOT call the original again (it would re-advance the bf_read).
                        *(int*) (dst + 0x10) = afterOriginal;
                        return originalResult;
                    }
                }

                if (ln <= MaxLogCount && _logger is { } log)
                {
                    log.LogInformation(
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

    // ── Fixed-width bf_write emit (bool/float32) ──────────────────────────────────────────────
    //
    //  Replicates the engine's inline bf_write::WriteUBitLong (little-endian bit packing) verified
    //  from EncodeFloat32 0x3ce8e0 / EncodeBool 0x3c4c00. bf_write layout:
    //    [bf+0x00] uint32* data
    //    [bf+0x0c] int     nMaxBits  (capacity)
    //    [bf+0x10] int     iCurBit   (cursor)
    //    [bf+0x20] byte    bOverflow
    //  Writes the low `numBits` of `value` at the cursor, advancing it. On insufficient room it
    //  sets the overflow flag exactly as the engine does and writes nothing — matching native
    //  behaviour so a substitute can never run past the buffer.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUBitLong(nint bf, uint value, int numBits)
    {
        var data    = *(uint**) (bf + 0x00);
        var maxBits = *(int*)  (bf + 0x0C);
        var cur     = *(int*)  (bf + 0x10);

        if (maxBits - cur < numBits)
        {
            *(int*)  (bf + 0x10) = maxBits;
            *(byte*) (bf + 0x20) = 1;
            return;
        }

        if (numBits < 32)
            value &= (1u << numBits) - 1u;

        *(int*) (bf + 0x10) = cur + numBits;

        var idx   = cur >> 5;
        var shift = cur & 0x1F;

        // Low dword: replace the `numBits` bits starting at `shift` (clamped to this dword).
        var mask0 = (shift == 0) ? 0xFFFFFFFFu : (0xFFFFFFFFu << shift);
        data[idx] = (data[idx] & ~mask0) | ((value << shift) & mask0);

        // High dword: only when the field straddles a 32-bit boundary.
        var bitsInFirst = 32 - shift;
        if (numBits > bitsInFirst)
        {
            var mask1 = (1u << (numBits - bitsInFirst)) - 1u;
            data[idx + 1] = (data[idx + 1] & ~mask1) | ((value >> bitsInFirst) & mask1);
        }
    }

    private static bool ValidateAddresses(ILogger logger)
    {
        var ok = true;
        if (GetBitRangeAddr    == 0) { logger.LogWarning("FieldSubstitution: GetBitRange address not resolved");    ok = false; }
        if (ValueCopyAddr      == 0) { logger.LogWarning("FieldSubstitution: ValueCopy address not resolved");      ok = false; }
        if (VarintWriterAddr   == 0) { logger.LogWarning("FieldSubstitution: VarintWriter address not resolved");   ok = false; }
        if (WriteFieldListAddr == 0) { logger.LogWarning("FieldSubstitution: WriteFieldList address not resolved"); ok = false; }
        return ok;
    }
}
