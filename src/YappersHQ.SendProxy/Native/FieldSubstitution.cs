using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

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
//        Third argument (uVar24) is the changed-field index.  We detour this
//        to capture the field index into a [ThreadStatic].
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
//  MODES:
//    Off    — pure passthrough, detours uninstalled.
//    Verify — detours installed; for proxied fields, logs cursor math + field
//             identity but writes NOTHING (dst cursor never rewound). Proves
//             field detection is correct with zero output corruption.
//    Fake   — full substitution: save/call-original/rewind/varint-write.
//
//  THREAD SAFETY:
//    All mutable state is either [ThreadStatic] (field index, serializer ptr)
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

    // ── [ThreadStatic] per-call context ─────────────────────────────────────

    // Set in GetBitRange detour, consumed in value-copy detour on the same thread.
    [ThreadStatic] private static uint _currentFieldIndex;    // changed-field index captured from GetBitRange arg3

    // Set in WriteFieldList context — the serializer ptr is WFL param_1.
    // We capture it in the GetBitRange hook (also inside WFL, same thread, same call).
    // GetBitRange is always called from WFL while WFL still has param_1 alive on the stack.
    // We obtain the serializer pointer from the WFL-entry hook installed in WriteFieldProbe.
    // But to avoid coupling, we capture it separately via a WFL-entry shim in Install().
    [ThreadStatic] private static nint _currentSerializer;    // CFlattenedSerializer* (WFL param_1 / rdi)

    // Log throttle for Verify + Fake first-N messages.
    private static int _logCount;
    private const  int MaxLogCount = 20;

    // Diagnostic: unconditional early log (first 50 value-copy calls in any non-Off mode).
    // Logs BEFORE _spoofs.TryGetValue so we see every call regardless of match.
    // Remove once field-index semantics are confirmed.
    private static int _vcopyDiagCount;
    private const  int VcopyDiagMax = 50;

    // ── Hooks ────────────────────────────────────────────────────────────────

    // GetBitRange hook: captures field index (3rd arg) + serializer ptr (from WFL-shim).
    private static IDetourHook? _getBitRangeHook;
    private static nint         _getBitRangeTrampoline;
    // Value-copy hook: performs the substitution.
    private static IDetourHook? _valueCopyHook;
    private static nint         _valueCopyTrampoline;

    // WFL shim: captures serializer ptr from WFL rdi each call.
    // Reuses WriteFieldProbe infrastructure if it's already installed, OR installs its own
    // lightweight shim that only captures param_1 + passes through, sharing the existing hook.
    // We install a SEPARATE lightweight WFL-entry hook here (independent of WriteFieldProbe)
    // because WriteFieldProbe may not be active during substitution mode.
    private static IDetourHook? _wflShimHook;
    private static nint         _wflShimTrampoline;

    // Native fn ptrs (resolved once on Install, never changed after).
    private static delegate* unmanaged[Cdecl]<nint, uint, void> _varintWriter;   // FUN_00500890
    private static ILogger? _logger;

    // ── Addresses (set by SendProxyModule before Install) ────────────────────

    public static nint GetBitRangeAddr;   // file-vaddr 0x326260
    public static nint ValueCopyAddr;     // file-vaddr 0x400b70
    public static nint VarintWriterAddr;  // file-vaddr 0x400890
    public static nint WriteFieldListAddr; // file-vaddr 0x343b60 (for WFL shim)

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

        // 2. GetBitRange hook — captures field index from 3rd argument.
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

        Interlocked.Exchange(ref _logCount, 0);
        return true;
    }

    public static void Uninstall()
    {
        Mode = SubstitutionMode.Off;

        _valueCopyHook?.Uninstall(); _valueCopyHook?.Dispose();   _valueCopyHook   = null; _valueCopyTrampoline  = 0;
        _getBitRangeHook?.Uninstall(); _getBitRangeHook?.Dispose(); _getBitRangeHook = null; _getBitRangeTrampoline = 0;
        _wflShimHook?.Uninstall(); _wflShimHook?.Dispose();       _wflShimHook     = null; _wflShimTrampoline    = 0;

        _logger?.LogInformation("FieldSubstitution: all hooks uninstalled");
        _logger = null;
    }

    // ── WFL shim ─────────────────────────────────────────────────────────────
    //
    //  Captures WFL param_1 (CFlattenedSerializer* = rdi) into _currentSerializer [ThreadStatic],
    //  then passes all 9 args through unchanged.
    //  The shim also ensures RecipientCapture.CurrentClient is available (it relies on the
    //  RecipientCapture hook being installed independently — its value is just read here).

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

    // ── GetBitRange hook ─────────────────────────────────────────────────────
    //
    //  ABI: FUN_00426260(appppsStack_18b58, DAT_00577a58, uVar24)
    //  rdi=arg1 (dst short-buffer), rsi=arg2 (descriptor), rdx=arg3 (field index, int).
    //  We only care about arg3 (the changed-field index).  Pure passthrough — no return value.

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void GetBitRangeHook(nint arg1, nint arg2, uint fieldIndex)
    {
        _currentFieldIndex = fieldIndex;
        ((delegate* unmanaged[Cdecl]<nint, nint, uint, void>) _getBitRangeTrampoline)(arg1, arg2, fieldIndex);
    }

    // ── Value-copy hook ───────────────────────────────────────────────────────
    //
    //  ABI: byte FUN_00500b70(bf_write* dst, bf_read* src, uint bitcount)
    //  rdi=dst, rsi=src, rdx=bitcount.
    //  bf_write cursor: *(int*)(dst + 0x10).  Confirmed from sp_bitprims.out:
    //    param_1[2] as long* = byte offset +0x10.

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
            // Resolve serializer name (needed for diag + spoof lookup).
            var serName = ReadShortAscii(*(nint*) (serPtr + 0x00), 48);

            // Decode the CFieldPath token and resolve field name.
            var token = _currentFieldIndex;
            DecodeToken(token, out var i0, out var i1, out var i2);
            var fieldName = ResolveFieldName(serPtr, token, i0, i1, i2);

            // ── Unconditional diagnostic (first VcopyDiagMax calls) ────────────
            // BEFORE any early-exit so we see every call regardless of match.
            // Logs token, decoded path, and resolved leaf name for verification.
            // Remove once field-index semantics are confirmed.
            {
                var diagN = Interlocked.Increment(ref _vcopyDiagCount);
                if (diagN <= VcopyDiagMax && _logger is { } diagLog)
                {
                    diagLog.LogInformation(
                        "VCOPY#{N} tid={Tid} ser=\"{SerName}\" token=0x{Token:X} path=[{I0},{I1},{I2}] name=\"{FieldName}\" bits={Bits} client=0x{Client:X}",
                        diagN,
                        Environment.CurrentManagedThreadId,
                        serName,
                        token,
                        i0, i1, i2,
                        fieldName,
                        bitcount,
                        RecipientCapture.CurrentClient);
                }
            }

            if (fieldName.Length == 0)
                goto Passthrough;

            // Look up the spoof registry.
            if (!_spoofs.TryGetValue((serName, fieldName), out var fakeValue))
                goto Passthrough;

            // Client identity (for future per-client divergence).
            var client = RecipientCapture.CurrentClient;

            var n = Interlocked.Increment(ref _logCount);

            if (mode == SubstitutionMode.Verify)
            {
                // VERIFY: call original normally (no rewind), but log the cursor math to
                // prove field detection + cursor reads are working correctly.
                int cursorBefore = (dst != 0) ? *(int*) (dst + 0x10) : -1;
                byte result = CallOriginal(dst, src, bitcount);
                int cursorAfter  = (dst != 0) ? *(int*) (dst + 0x10) : -1;

                if (n <= MaxLogCount && _logger is { } log)
                {
                    log.LogInformation(
                        "SUBST-VERIFY field=\"{Ser}::{Field}\" client=0x{Client:X} bitcount={Bits} "
                        + "cursorBefore={Before} cursorAfter={After} (fake would be {Fake})",
                        serName, fieldName, client, bitcount, cursorBefore, cursorAfter, fakeValue);
                }
                return result;
            }
            else  // Fake
            {
                // FAKE: save dst cursor, call original (advances src + dst, writes real bits),
                // rewind dst cursor, write our zigzag-encoded value via the varint writer.
                int savedCursor = *(int*) (dst + 0x10);
                byte originalResult = CallOriginal(dst, src, bitcount);

                // Rewind dst cursor to where it was before — our write starts here.
                *(int*) (dst + 0x10) = savedCursor;

                // Zigzag-encode the fake value: (uint)((v<<1)^(v>>31))
                uint zigzag = (uint) ((fakeValue << 1) ^ (fakeValue >> 31));
                _varintWriter(dst, zigzag);

                if (n <= MaxLogCount && _logger is { } log)
                {
                    log.LogInformation(
                        "SUBST-FAKE field=\"{Ser}::{Field}\" client=0x{Client:X} bitcount={Bits} "
                        + "savedCursor={Saved} fakeValue={Fake} zigzag=0x{Zz:X}",
                        serName, fieldName, client, bitcount, savedCursor, fakeValue, zigzag);
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
    ///     Decode a packed CFieldPath token into up to three 0-based level indices.
    ///     Token format (verified against CCSPlayerPawn examples):
    ///       token = ((i0+1)&lt;&lt;22) | ((i1+1)&lt;&lt;11) | (i2+1)
    ///       A level is absent when its decoded value is -1 (stored as 0 = bias-1 encoding).
    ///       Special sentinel: 0xFFFFFFFF = root (skip).
    /// </summary>
    private static void DecodeToken(uint token, out int i0, out int i1, out int i2)
    {
        i0 = (int) ((token >> 22) & 0x7FF) - 1;
        i1 = (int) ((token >> 11) & 0x7FF) - 1;
        i2 = (int) ( token        & 0x7FF) - 1;
    }

    /// <summary>
    ///     Resolve the leaf field name from a CFlattenedSerializer and a decoded CFieldPath token.
    ///
    ///     Serializer layout (confirmed by WriteFieldProbe + SerializerProbe + encodefield.out):
    ///       serializer+0x08 = field count (int32)
    ///       serializer+0x10 = field array base — array of POINTERS, stride 8
    ///       field_record+0x08 = char* field name (leaf) OR CFlattenedSerializer* child (nested)
    ///
    ///     Token descent (from encodefield.out — EncodeField recursive call pattern):
    ///       Level 0: recPtr = *(fieldArray + i0*8)
    ///       Level 1: childSer = *(recPtr + 0x08); recPtr = *(childFieldArray + i1*8)
    ///       Level 2: childSer2 = *(recPtr + 0x08); recPtr = *(childFieldArray2 + i2*8)
    ///       Leaf name: ReadShortAscii(*(recPtr + 0x08))
    ///
    ///     Child serializer offset confirmed from encodefield.out:
    ///       puStack_2a0 = (undefined *)plVar40[1]  → inline_record+0x08 = child CFlattenedSerializer*
    ///       This is then passed as param_1 (the serializer) in the recursive EncodeField call.
    ///       For leaf fields, record+0x08 is the char* field name.
    ///       For nested fields, record+0x08 is the child CFlattenedSerializer* (its own +0x00 is
    ///       the child serializer name, +0x08 count, +0x10 child field array).
    ///
    ///     All pointer dereferences are guarded by IsUserPtr checks and a try/catch.
    ///     Returns "" on any fault (bounds-check, invalid pointer, or exception).
    /// </summary>
    private static string ResolveFieldName(nint serializer, uint token, int i0, int i1, int i2)
    {
        // token 0xFFFFFFFF = root sentinel (skip); token 0 = absent (all levels -1 after decode)
        if (token == 0xFFFFFFFF || token == 0 || i0 < 0)
            return string.Empty;

        try
        {
            // ── Level 0 ─────────────────────────────────────────────────────────
            var count0   = *(int*) (serializer + 0x08);
            if (count0 <= 0 || i0 >= count0 || count0 > 4096)
                return string.Empty;

            var arr0 = *(nint*) (serializer + 0x10);
            if (!IsUserPtr(arr0))
                return string.Empty;

            var rec0 = *(nint*) (arr0 + i0 * 8);
            if (!IsUserPtr(rec0))
                return string.Empty;

            if (i1 < 0)
            {
                // Leaf at level 0 — record+0x08 is the char* field name.
                return ReadShortAscii(*(nint*) (rec0 + 0x08), 48);
            }

            // ── Level 1 descent: record+0x08 = child CFlattenedSerializer* ─────
            var child1 = *(nint*) (rec0 + 0x08);
            if (!IsUserPtr(child1))
                return string.Empty;

            var count1 = *(int*) (child1 + 0x08);
            if (count1 <= 0 || i1 >= count1 || count1 > 4096)
                return string.Empty;

            var arr1 = *(nint*) (child1 + 0x10);
            if (!IsUserPtr(arr1))
                return string.Empty;

            var rec1 = *(nint*) (arr1 + i1 * 8);
            if (!IsUserPtr(rec1))
                return string.Empty;

            if (i2 < 0)
            {
                // Leaf at level 1.
                return ReadShortAscii(*(nint*) (rec1 + 0x08), 48);
            }

            // ── Level 2 descent: record+0x08 = child CFlattenedSerializer* ─────
            var child2 = *(nint*) (rec1 + 0x08);
            if (!IsUserPtr(child2))
                return string.Empty;

            var count2 = *(int*) (child2 + 0x08);
            if (count2 <= 0 || i2 >= count2 || count2 > 4096)
                return string.Empty;

            var arr2 = *(nint*) (child2 + 0x10);
            if (!IsUserPtr(arr2))
                return string.Empty;

            var rec2 = *(nint*) (arr2 + i2 * 8);
            if (!IsUserPtr(rec2))
                return string.Empty;

            // Leaf at level 2.
            return ReadShortAscii(*(nint*) (rec2 + 0x08), 48);
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
        if (GetBitRangeAddr   == 0) { logger.LogWarning("FieldSubstitution: GetBitRange address not resolved");   ok = false; }
        if (ValueCopyAddr     == 0) { logger.LogWarning("FieldSubstitution: ValueCopy address not resolved");     ok = false; }
        if (VarintWriterAddr  == 0) { logger.LogWarning("FieldSubstitution: VarintWriter address not resolved");  ok = false; }
        if (WriteFieldListAddr == 0){ logger.LogWarning("FieldSubstitution: WriteFieldList address not resolved"); ok = false; }
        return ok;
    }
}
