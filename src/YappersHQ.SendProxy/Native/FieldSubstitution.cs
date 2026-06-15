using System;
using System.Collections.Concurrent;
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
//  HOW IT WORKS (verified against sp_wfl.out + sp_bitprims.out):
//
//  WriteFieldList (WFL) runs once per changed-field per client on a send thread.
//  Inside its per-field loop it calls TWO functions back-to-back:
//
//    1.  GetBitRange  (FUN_00426260, file-vaddr 0x326260):
//            FUN_00426260(appppsStack_18b58, DAT_00577a58, uVar24)
//        rdi=arg1 (CFieldPath* pathOut — WFL stack buffer), rsi=arg2 (descriptor
//        table), rdx=arg3 (opaque registry index — NOT a bit-packed field path).
//        We detour to CALL THE ORIGINAL FIRST (so pathOut gets filled by the
//        engine), then capture arg1 into [ThreadStatic] _currentFieldPath.
//        The pathOut buffer remains valid through the following value-copy call
//        on the same thread.
//
//    2.  Value-copy primitive  (FUN_00500b70, file-vaddr 0x400b70):
//            FUN_00500b70(&puStack_18b38, &lStack_18b08, iVar3 - iVar30)
//        Copies `bitcount` bits from the shared pack-buf into WFL's local
//        intermediate bf_write (puStack_18b38 — NOT the client bf_write).
//        Signature: byte FUN_00500b70(bf_write* dst, bf_read* src, uint bitcount).
//        bf_write cursor: *(int*)(dst + 0x10).
//
//  For substitution in the value-copy hook:
//    a)  Save dst cursor before call.
//    b)  Call original trampoline (advances src + dst cursors, writes real bits).
//    c)  Rewind dst cursor to saved value.
//    d)  Write our zigzag-encoded fake value via FUN_00500890 (varint writer).
//
//  Varint writer (FUN_00500890, file-vaddr 0x400890):
//    void(bf_write* dst, uint32 zigzag)
//    rdi=dst, rsi=zigzag.  Zigzag encoding: (uint)((v<<1)^(v>>31)).
//
//  CFieldPath header layout (verified by RE of GetBitRange output buffer):
//    hdr+0x00..0x0F : up to 8 inline short indices (when read-only flag is 0)
//    hdr+0x18       : count (short) — number of valid path levels (0..7)
//    hdr+0x1A       : read-only flag (byte); when != 0 the first 8 bytes are a
//                     pointer to an external short[] rather than inline indices
//    Sentinel: 0x7FFF at idx[0] means no valid path (return "").
//
//  MODES:
//    Off    — pure passthrough, detours uninstalled.
//    Verify — detours installed; for proxied fields, logs cursor math + field
//             identity but writes NOTHING (dst cursor never rewound). Proves
//             field detection is correct with zero output corruption.
//    Fake   — full substitution: save/call-original/rewind/varint-write.
//
//  THREAD SAFETY:
//    All mutable state is either [ThreadStatic] (field path ptr, serializer ptr)
//    or read-only after Install() (fn ptrs, spoof table snapshot per-call).
//    _spoofs is a ConcurrentDictionary — safe for concurrent set + read.
//
// ─────────────────────────────────────────────────────────────────────────────

internal enum SubstitutionMode { Off, Verify, Fake }

internal static unsafe class FieldSubstitution
{
    // ── Mode ─────────────────────────────────────────────────────────────────

    private static volatile int _mode = (int) SubstitutionMode.Off;   // SubstitutionMode (int for Volatile)
    public static SubstitutionMode Mode
    {
        get => (SubstitutionMode) _mode;
        set => Interlocked.Exchange(ref _mode, (int) value);
    }

    // ── Spoof registry ───────────────────────────────────────────────────────

    // Key: (serializerName, fieldName) → fake int32 value.
    // ConcurrentDictionary so SetSpoof/ClearSpoofs are safe while hooks run.
    private static readonly ConcurrentDictionary<(string ser, string field), int> _spoofs = new();

    public static void SetSpoof(string serializerName, string fieldName, int value)
        => _spoofs[(serializerName, fieldName)] = value;

    public static void ClearSpoofs() => _spoofs.Clear();
    public static bool HasSpoofs    => !_spoofs.IsEmpty;

    // ── Per-client callback registry ─────────────────────────────────────────
    //
    //  Key: (serializerName, fieldName) → PerClientIntProxy delegate.
    //  When a callback is registered for a field, it is invoked INSTEAD of (or in addition to)
    //  the uniform spoof.  The callback receives:
    //    client      — raw CServerSideClient* (from RecipientCapture, 0 if not captured)
    //    entityIndex — from WDE ctx+0x34 (captured per-entity in this thread, -1 if unknown)
    //    value       — ref int, pre-seeded with the uniform spoof value (if any, else 0)
    //  Return true → substitute the (possibly modified) value; false → pass through original.
    //
    //  THREAD SAFETY: ConcurrentDictionary; callbacks run on ~6 engine worker threads.
    //  Consumers MUST ensure their callbacks are thread-safe and do NOT block.
    //
    //  EXCEPTION SAFETY: a throwing callback is silently caught → passthrough for that field.

    private static readonly ConcurrentDictionary<(string ser, string field), PerClientIntProxy> _callbacks = new();

    /// <summary>
    ///     Register a per-client callback for a (serializerName, fieldName) pair.
    ///     Replaces any previously registered callback for the same key.
    ///     The callback MUST be thread-safe and fast — it runs on engine send threads.
    /// </summary>
    public static void SetCallback(string serializerName, string fieldName, PerClientIntProxy callback)
        => _callbacks[(serializerName, fieldName)] = callback;

    /// <summary>Remove a per-client callback for a (serializerName, fieldName) pair.</summary>
    public static void ClearCallback(string serializerName, string fieldName)
        => _callbacks.TryRemove((serializerName, fieldName), out _);

    public static void ClearCallbacks() => _callbacks.Clear();
    public static bool HasCallbacks     => !_callbacks.IsEmpty;

    // ── [ThreadStatic] per-call context ─────────────────────────────────────

    // Set in GetBitRange detour AFTER the original runs (so pathOut is filled).
    // Points to the WFL stack buffer that holds the decoded CFieldPath for this field.
    // Remains valid through the value-copy call that immediately follows on the same thread.
    [ThreadStatic] private static nint _currentFieldPath;   // CFieldPath* (arg1 of GetBitRange, post-fill)

    // Set in WriteFieldList context — the serializer ptr is WFL param_1.
    // We capture it in the WFL shim installed in Install().
    [ThreadStatic] private static nint _currentSerializer;  // CFlattenedSerializer* (WFL param_1 / rdi)

    // Set in the WDE entity-index capture hook: *(int*)(ctx+0x34) from WriteDeltaEntity_Internal.
    // Valid for the duration of one entity's full encode (all WFL+value-copy calls for that entity).
    // Cleared back to -1 after the WDE trampoline returns.
    [ThreadStatic] private static int _currentEntityIndex;  // entity index (-1 = unknown)

    // Diagnostic: log first N distinct (ser, leaf) pairs seen.
    private static int _diagCount;
    private const  int MaxDiagCount = 25;
    private static readonly ConcurrentDictionary<(string, string), byte> _diagSeen = new();

    // Log throttle for Verify + Fake first-N messages.
    private static int _logCount;
    private const  int MaxLogCount = 20;

    // ── Hooks ────────────────────────────────────────────────────────────────

    // GetBitRange hook: calls original first, then captures filled pathOut ptr.
    private static IDetourHook? _getBitRangeHook;
    private static nint         _getBitRangeTrampoline;
    // Value-copy hook: performs the substitution.
    private static IDetourHook? _valueCopyHook;
    private static nint         _valueCopyTrampoline;

    // WFL shim: captures serializer ptr from WFL rdi each call.
    private static IDetourHook? _wflShimHook;
    private static nint         _wflShimTrampoline;

    // WDE entity-index capture: reads *(int*)(ctx+0x34) into _currentEntityIndex [ThreadStatic].
    private static IDetourHook? _wdeEntityCaptureHook;
    private static nint         _wdeEntityCaptureTrampoline;

    // Native fn ptrs (resolved once on Install, never changed after).
    private static delegate* unmanaged[Cdecl]<nint, uint, void> _varintWriter;   // FUN_00500890
    private static ILogger? _logger;

    // ── Addresses (set by SendProxyModule before Install) ────────────────────

    public static nint GetBitRangeAddr;    // file-vaddr 0x326260
    public static nint ValueCopyAddr;      // file-vaddr 0x400b70
    public static nint VarintWriterAddr;   // file-vaddr 0x400890
    public static nint WriteFieldListAddr; // file-vaddr 0x343b60 (for WFL shim)
    public static nint WdeAddr;            // CNetworkGameServerBase::WriteDeltaEntity_Internal (for entity index capture; 0 = skip)

    // ── Install / Uninstall ──────────────────────────────────────────────────

    public static bool Install(InterfaceBridge bridge, ILogger logger)
    {
        _logger = logger;

        if (!ValidateAddresses(logger))
            return false;

        // Resolve varint writer fn ptr (constant after this).
        _varintWriter = (delegate* unmanaged[Cdecl]<nint, uint, void>) VarintWriterAddr;

        // 1. WFL shim — captures serializer ptr from WFL rdi into _currentSerializer [ThreadStatic].
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
            _wflShimHook        = wflHook;
            _wflShimTrampoline  = wflHook.Trampoline;
            logger.LogInformation("FieldSubstitution: WFL shim installed @ 0x{Addr:X}", WriteFieldListAddr);
        }

        // 2. GetBitRange hook — calls original first (fills pathOut), then captures arg1.
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
            _getBitRangeHook        = gbrHook;
            _getBitRangeTrampoline  = gbrHook.Trampoline;
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
            _valueCopyHook        = vcHook;
            _valueCopyTrampoline  = vcHook.Trampoline;
            logger.LogInformation("FieldSubstitution: value-copy hook installed @ 0x{Addr:X}", ValueCopyAddr);
        }

        // 4. WDE entity-index capture — optional; skip gracefully if address not set.
        //    Hooks WriteDeltaEntity_Internal (rdi=this, rsi=ctx), reads *(int*)(ctx+0x34) into
        //    _currentEntityIndex [ThreadStatic] before the trampoline, clears to -1 after.
        //    This gives per-entity context to the value-copy hook for per-client callbacks.
        if (_wdeEntityCaptureHook is null && WdeAddr != 0)
        {
            var wdeHook   = bridge.HookManager.CreateDetourHook();
            var wdeHookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) &WdeEntityCaptureHook;
            wdeHook.Prepare(WdeAddr, wdeHookFn);
            if (wdeHook.Install())
            {
                _wdeEntityCaptureHook        = wdeHook;
                _wdeEntityCaptureTrampoline  = wdeHook.Trampoline;
                logger.LogInformation("FieldSubstitution: WDE entity-index capture installed @ 0x{Addr:X}", WdeAddr);
            }
            else
            {
                // Non-fatal: per-client callbacks still work but entityIndex will be -1.
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

        _valueCopyHook?.Uninstall(); _valueCopyHook?.Dispose();               _valueCopyHook            = null; _valueCopyTrampoline            = 0;
        _getBitRangeHook?.Uninstall(); _getBitRangeHook?.Dispose();           _getBitRangeHook          = null; _getBitRangeTrampoline          = 0;
        _wflShimHook?.Uninstall(); _wflShimHook?.Dispose();                   _wflShimHook              = null; _wflShimTrampoline              = 0;
        _wdeEntityCaptureHook?.Uninstall(); _wdeEntityCaptureHook?.Dispose(); _wdeEntityCaptureHook     = null; _wdeEntityCaptureTrampoline     = 0;

        _logger?.LogInformation("FieldSubstitution: all hooks uninstalled");
        _logger = null;
    }

    // ── WFL shim ─────────────────────────────────────────────────────────────
    //
    //  Captures WFL param_1 (CFlattenedSerializer* = rdi) into _currentSerializer [ThreadStatic],
    //  then passes all 9 args through unchanged.

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
    //
    //  ABI: rdi=a (CNetworkGameServerBase*), rsi=b (delta ctx*).
    //  *(int*)(b+0x34) = entityIndex (verified by WriteDeltaProbe RE).
    //
    //  Strategy: read entityIndex BEFORE calling the trampoline; restore _currentEntityIndex
    //  to -1 AFTER (in finally) so no stale index leaks to the next entity on this thread.
    //  6 args declared (like WriteDeltaProbe / RecipientCapture) for ABI correctness.

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint WdeEntityCaptureHook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        // Read entity index from ctx+0x34 before calling original.
        // Guard: b must be a plausible heap pointer; read is exception-guarded.
        var entityIndex = -1;
        if (IsUserPtr(b))
        {
            try { entityIndex = *(int*) (b + 0x34); }
            catch { /* best-effort; -1 sentinel used if read fails */ }
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
            // Always clear — prevents stale entity index leaking to unrelated WFL calls
            // that might run on this thread after this entity's encode completes.
            _currentEntityIndex = -1;
        }
        return result;
    }

    // ── GetBitRange hook ─────────────────────────────────────────────────────
    //
    //  ABI: FUN_00426260(pathOut /*rdi*/, table /*rsi*/, registryIndex /*rdx*/)
    //  arg1 (rdi) = CFieldPath* — WFL stack buffer that receives the decoded path.
    //  arg3 (rdx) = opaque registry index — NOT a bit-packed CFieldPath; do NOT decode it.
    //
    //  Strategy: call the original FIRST so the engine fills pathOut, then capture arg1.
    //  The buffer remains valid on this thread until after the value-copy call.

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GetBitRangeHook(nint pathOut, nint table, uint registryIndex)
    {
        // Call original first — this fills the CFieldPath at pathOut.
        ((delegate* unmanaged[Cdecl]<nint, nint, uint, void>) _getBitRangeTrampoline)(pathOut, table, registryIndex);
        // Capture the now-filled path buffer for the immediately following value-copy call.
        _currentFieldPath = pathOut;
    }

    // ── Value-copy hook ───────────────────────────────────────────────────────
    //
    //  ABI: byte FUN_00500b70(bf_write* dst, bf_read* src, uint bitcount)
    //  rdi=dst, rsi=src, rdx=bitcount.
    //  bf_write cursor: *(int*)(dst + 0x10).  Confirmed from sp_bitprims.out.

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte ValueCopyHook(nint dst, nint src, uint bitcount)
    {
        var mode = (SubstitutionMode) _mode;
        if (mode == SubstitutionMode.Off)
            goto Passthrough;

        // Only act when inside a WFL call (serializer ptr is valid) and the serializer
        // pointer is a plausible heap address.
        var serPtr = _currentSerializer;
        if (!IsUserPtr(serPtr))
            goto Passthrough;

        try
        {
            // Resolve serializer name from the serializer's own name field.
            var serName = ReadShortAscii(*(nint*) (serPtr + 0x00), 48);

            // Resolve the leaf field name from the CFieldPath buffer filled by GetBitRange.
            var fieldName = ResolveFieldName(serPtr, _currentFieldPath);

            // Emit diagnostic log for the first MaxDiagCount distinct (ser, leaf) pairs.
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
                        catch { /* diag log must never crash */ }
                    }
                }
            }

            if (fieldName.Length == 0)
                goto Passthrough;

            // Look up spoof registry and per-client callback registry.
            _spoofs.TryGetValue((serName, fieldName), out var uniformFakeValue);
            _callbacks.TryGetValue((serName, fieldName), out var clientCallback);

            // If neither a uniform spoof nor a callback is registered, pass through.
            if (clientCallback is null && !_spoofs.ContainsKey((serName, fieldName)))
                goto Passthrough;

            // Client identity and entity index (both [ThreadStatic], valid on this thread).
            var client      = RecipientCapture.CurrentClient;
            var entityIndex = _currentEntityIndex;

            var ln = Interlocked.Increment(ref _logCount);

            if (mode == SubstitutionMode.Verify)
            {
                // VERIFY: call original normally (no rewind), but log cursor math to
                // prove field detection + cursor reads are working correctly.
                int cursorBefore = (dst != 0) ? *(int*) (dst + 0x10) : -1;
                byte result = CallOriginal(dst, src, bitcount);
                int cursorAfter  = (dst != 0) ? *(int*) (dst + 0x10) : -1;

                if (ln <= MaxLogCount && _logger is { } log)
                {
                    log.LogInformation(
                        "SUBST-VERIFY field=\"{Ser}::{Field}\" client=0x{Client:X} ent={Ent} bitcount={Bits} "
                        + "cursorBefore={Before} cursorAfter={After} (fake would be {Fake})",
                        serName, fieldName, client, entityIndex, bitcount, cursorBefore, cursorAfter, uniformFakeValue);
                }
                return result;
            }
            else  // Fake
            {
                // Determine the effective fake value:
                //   1. Start with the uniform spoof value (or 0 if none registered).
                //   2. If a per-client callback is registered, invoke it — it can overwrite
                //      the value and return true (substitute) or false (pass through original).
                //
                // EXCEPTION SAFETY: a throwing callback must NOT crash the send path.
                //   On exception → passthrough for this field on this client.

                int effectiveFake   = uniformFakeValue;  // pre-seeded (0 if no uniform spoof)
                bool shouldSubstitute = clientCallback is null; // true when only uniform spoof; overridden below

                if (clientCallback is not null)
                {
                    try
                    {
                        shouldSubstitute = clientCallback(client, entityIndex, ref effectiveFake);
                    }
                    catch (Exception ex)
                    {
                        // Log once on first exception per field, then suppress.
                        if (ln <= MaxLogCount && _logger is { } errLog)
                        {
                            try
                            {
                                errLog.LogWarning(ex,
                                    "SUBST-FAKE per-client callback threw for \"{Ser}::{Field}\" "
                                    + "client=0x{Client:X} ent={Ent} — passing through original",
                                    serName, fieldName, client, entityIndex);
                            }
                            catch { /* logging must never fault the hook */ }
                        }
                        // Pass through original — do NOT substitute.
                        return CallOriginal(dst, src, bitcount);
                    }
                }

                if (!shouldSubstitute)
                    return CallOriginal(dst, src, bitcount);

                // FAKE: save dst cursor, call original (advances src + dst, writes real bits),
                // rewind dst cursor, write our zigzag-encoded value via the varint writer.
                int savedCursor = *(int*) (dst + 0x10);
                byte originalResult = CallOriginal(dst, src, bitcount);

                // Rewind dst cursor to where it was before — our write starts here.
                *(int*) (dst + 0x10) = savedCursor;

                // Zigzag-encode the effective fake value: (uint)((v<<1)^(v>>31))
                uint zigzag = (uint) ((effectiveFake << 1) ^ (effectiveFake >> 31));
                _varintWriter(dst, zigzag);

                if (ln <= MaxLogCount && _logger is { } log)
                {
                    log.LogInformation(
                        "SUBST-FAKE field=\"{Ser}::{Field}\" client=0x{Client:X} ent={Ent} bitcount={Bits} "
                        + "savedCursor={Saved} effectiveFake={Fake} zigzag=0x{Zz:X}",
                        serName, fieldName, client, entityIndex, bitcount, savedCursor, effectiveFake, zigzag);
                }
                return originalResult;
            }
        }
        catch
        {
            // Never throw out of an unmanaged callback — fall through to passthrough.
        }

        Passthrough:
        return CallOriginal(dst, src, bitcount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte CallOriginal(nint dst, nint src, uint bitcount)
        => ((delegate* unmanaged[Cdecl]<nint, nint, uint, byte>) _valueCopyTrampoline)(dst, src, bitcount);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Resolve the leaf field name from a CFlattenedSerializer and the CFieldPath buffer
    ///     that was filled by GetBitRange (arg1 / rdi of that function, captured post-original-call).
    ///
    ///     CFieldPath header layout:
    ///       hdr+0x00..0x0F : inline short[8] indices (when read-only byte at hdr+0x1A == 0)
    ///                        OR: *(nint*)(hdr+0x00) is a pointer to an external short[] (read-only != 0)
    ///       hdr+0x18       : count (short) — number of valid path levels; valid range 1..7
    ///       hdr+0x1A       : read-only flag (byte)
    ///       idx[0] == 0x7FFF → sentinel / empty path; return "".
    ///
    ///     Flattened-serializer field array (stride 0x2E, inline):
    ///       arrayBase = *(nint*)(serializer + 0x30)
    ///       record_i  = arrayBase + i * 0x2E
    ///       record+0x00 = CNetworkSerializerFieldInfo* (leaf name at fieldInfo+0x08)
    ///       record+0x08 = CFlattenedSerializer* child (for descent)
    ///
    ///     NOTE: when EncodeField recurses into a sub-serializer, _currentSerializer may be the
    ///     CHILD serializer (e.g. "CCSPlayer_WeaponServices") with a path relative to that child.
    ///     The (serName, leafName) registry key uses the serializer's OWN name — which is correct
    ///     because the diag log reveals exactly what serName + leafName pair each field resolves to.
    ///
    ///     All dereferences are IsUserPtr-gated; entire function is wrapped in try/catch → "" on fault.
    /// </summary>
    private static string ResolveFieldName(nint serializer, nint hdr)
    {
        if (!IsUserPtr(serializer) || !IsUserPtr(hdr))
            return string.Empty;

        try
        {
            // Read count from hdr+0x18.
            var count = *(short*) (hdr + 0x18);
            if (count <= 0 || count > 7)
                return string.Empty;

            // Determine index array base: inline (hdr itself) or indirected (read-only flag).
            nint idxArr;
            if (*(byte*) (hdr + 0x1A) != 0)
            {
                // Read-only: first 8 bytes of hdr hold a pointer to the external short[].
                idxArr = *(nint*) hdr;
                if (!IsUserPtr(idxArr))
                    return string.Empty;
            }
            else
            {
                // Inline: indices are at hdr+0x00.
                idxArr = hdr;
            }

            // Read level-0 index.
            var idx0 = *(short*) (idxArr + 0 * 2);
            if (idx0 == 0x7FFF)
                return string.Empty;

            // ── Level 0 ─────────────────────────────────────────────────────
            var arr0 = *(nint*) (serializer + 0x30);
            if (!IsUserPtr(arr0))
                return string.Empty;

            var rec = arr0 + idx0 * 0x2E;

            // Descend for levels 1..count-1.
            for (var k = 1; k < count; k++)
            {
                var idxK = *(short*) (idxArr + k * 2);
                if (idxK == 0x7FFF)
                    break;

                var child = *(nint*) (rec + 0x08);
                if (!IsUserPtr(child))
                    return string.Empty;

                var arrK = *(nint*) (child + 0x30);
                if (!IsUserPtr(arrK))
                    return string.Empty;

                rec = arrK + idxK * 0x2E;
            }

            // Leaf: read name from fieldInfo at rec+0x00, name char* at fieldInfo+0x08.
            var pInfo = *(nint*) (rec + 0x00);
            if (!IsUserPtr(pInfo))
                return string.Empty;

            return ReadShortAscii(*(nint*) (pInfo + 0x08), 48);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsUserPtr(nint p) => p != 0 && ((ulong) p >> 40) == 0x7F;

    private static string ReadShortAscii(nint p, int maxLen)
    {
        if (!IsUserPtr(p))
            return string.Empty;
        try
        {
            var buf = stackalloc byte[maxLen + 1];
            var len = 0;
            for (; len < maxLen; len++)
            {
                var ch = *(byte*) (p + len);
                if (ch == 0) break;
                if (ch < 0x20 || ch > 0x7E) return string.Empty;
                buf[len] = ch;
            }
            buf[len] = 0;
            return len == 0 ? string.Empty
                : new string((sbyte*) buf, 0, len, System.Text.Encoding.ASCII);
        }
        catch { return string.Empty; }
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
