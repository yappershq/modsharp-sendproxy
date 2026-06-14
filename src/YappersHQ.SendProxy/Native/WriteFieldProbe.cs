using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     VERIFY-MODE (read-only, no substitution) probe detour on
///     <c>CFlattenedSerializer::WriteFieldList</c> (libnetworksystem.so, file-RVA 0x343b60,
///     Ghidra FUN_00443b60 with image-base 0x100000).
///
///     <para><b>WHY this function?</b></para>
///     <para>
///         <c>WriteFieldList</c> is the per-client encode loop: for each changed field index
///         it calls <c>GetBitRange</c> (FUN_00426260) to find the field's pre-encoded bit
///         span in the shared pack buffer, then calls the generic bit-copy primitive
///         <c>FUN_00500b70(dst_bf_write*, src_bf_read*, bitcount)</c> to copy those bits
///         into the client's per-client <c>bf_write</c>. The bit-copy primitive has NO field
///         identity — it is a pure bulk copy. Therefore the substitution point MUST be inside
///         WriteFieldList, which owns both (a) field identity (<c>uVar24</c> = changed-field
///         index → resolved to a field record via the serializer field array on <c>param_1</c>)
///         and (b) the destination <c>bf_write</c> (local <c>puStack_18b38</c> that is
///         eventually flushed into <c>param_4</c>, the client's message buffer). Any per-field
///         substitution logic must either:
///         <list type="bullet">
///             <item>Hook WriteFieldList's entry and reimplement/wrap the per-field loop, OR</item>
///             <item>Patch the pre-encoded shared buffer before WriteFieldList runs (but that
///                   is non-trivial for per-client divergence).</item>
///         </list>
///         The cleanest Phase-2 substitution path is a WriteFieldList-entry detour where, for
///         any field with a registered send-proxy, we decode the pre-encoded value from the
///         shared buffer, invoke the callback, re-encode the (possibly modified) value into a
///         scratch buffer, then reconstruct a patched field-list argument so the original bit-
///         copy loop copies the patched bits.  VERIFY mode here confirms the entry fires per
///         field-list encode and that <c>RecipientCapture.CurrentClient</c> is readable.
///     </para>
///
///     <para><b>ABI (SysV x86-64)</b> — 9 parameters, 3 on the stack:</para>
///     <code>
///         rdi = param_1  (CFlattenedSerializer* — the serializer object; serializer name @ +0x00,
///                          field-count @ +0x08, field-array @ +0x10; stride 8 bytes per entry)
///         rsi = param_2  (undefined8 — a flags/context qword, usage TBD)
///         rdx = param_3  (long — pointer to the serializer's encoder block / field-type dispatch)
///         rcx = param_4  (long — destination bf_write* for this client)
///         r8  = param_5  (long* — source: shared pack-buffer descriptor incl. field-index array)
///         r9  = param_6  (uint — entity class index / delta tick)
///         [rsp+0x08] = param_7  (uint — unk, related to frame count)
///         [rsp+0x10] = param_8  (int* — changed-field index array / count — key field filter)
///         [rsp+0x18] = param_9  (uint — unk, snapshot/tick related)
///     </code>
///
///     <para>
///         All 9 args are relayed to the trampoline unchanged (VERIFY mode = pure passthrough).
///         Stack args 7-9 are declared in the unmanaged delegate so the C call frame is correct
///         and the original function reads the right values (this is what crashed the
///         EncodeField-entry probe that used only 6 args).
///     </para>
///
///     <para><b>Sig (nosoop makesig, 24 bytes, MATCHES:1 in libnetworksystem, build 2026-06-02):</b></para>
///     <code>55 48 8D 05 ? ? ? ? 45 31 DB 66 0F EF C0 48 89 E5 41 57 41 56 4C 8D</code>
///     <para>
///         Bytes 4-7 are wildcarded (rip-relative displacement in <c>lea rax,[rip+off]</c>
///         at the prologue — shifts on relink).  The surrounding bytes are stable prologue
///         instructions that are unique in the binary.
///     </para>
/// </summary>
internal static unsafe class WriteFieldProbe
{
    private static IDetourHook? _hook;
    private static nint         _trampoline;
    private static ILogger?     _logger;
    private static int          _count;       // log throttle (first ~30)

    // Cheap user-space range gate: valid Linux x64 heap/rodata ptrs have bits [63:40] == 0x7F.
    private static bool IsUserPtr(nint p) => p > 0 && ((ulong) p >> 40) == 0x7F;

    // ── Install / Uninstall ───────────────────────────────────────────────────────────────────

    public static bool Install(InterfaceBridge bridge, ILogger logger, nint wflAddr)
    {
        if (_hook is not null)
        {
            logger.LogInformation("WriteFieldProbe: already installed");
            return true;
        }
        if (wflAddr == 0)
        {
            logger.LogWarning("WriteFieldProbe: null target address — cannot install");
            return false;
        }

        _logger = logger;
        _count  = 0;

        var hook   = bridge.HookManager.CreateDetourHook();
        // 9 args: 6 register (rdi..r9) + 3 stack (param_7..param_9).  Matching the full ABI is
        // critical — if we only pass 6 args in the trampoline call the original reads garbage from
        // the stack (same crash mode as the EncodeField-entry probe).  Declare all 9 so the
        // managed→unmanaged call sites emit the correct x86-64 SysV frame with 3 stack pushes.
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<
            nint, nint, nint, nint, nint, uint,   // rdi..r9  (param_1..6)
            uint, nint, uint,                      // [rsp+8]..[rsp+18] (param_7..9)
            nint>) &Hook;
        hook.Prepare(wflAddr, hookFn);
        if (!hook.Install())
        {
            logger.LogWarning("WriteFieldProbe: IDetourHook.Install() failed");
            return false;
        }

        _hook       = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("WriteFieldProbe installed @ 0x{Addr:X} (trampoline=0x{Tr:X})",
            wflAddr, _trampoline);
        return true;
    }

    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook?.Dispose();
        _hook       = null;
        _trampoline = 0;
        _logger?.LogInformation("WriteFieldProbe uninstalled");
        _logger = null;
    }

    // ── Hook ─────────────────────────────────────────────────────────────────────────────────

    // ABI: rdi=a (CFlattenedSerializer*), rsi=b, rdx=c, rcx=d (dst bf_write*),
    //       r8=e (src pack-buf desc*), r9=p6, then three stack args p7..p9.
    //
    // The hook only reads RecipientCapture.CurrentClient (a [ThreadStatic] — no args touched),
    // and optionally sniffs param_1/param_8 to log field info. Pure passthrough in verify mode.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(
        nint a,    // rdi — CFlattenedSerializer*
        nint b,    // rsi — context qword
        nint c,    // rdx — encoder-block ptr
        nint d,    // rcx — dst bf_write* (per-client)
        nint e,    // r8  — src pack-buf descriptor (long*)
        uint p6,   // r9  — entity class index / delta tick
        uint p7,   // [rsp+0x08]
        nint p8,   // [rsp+0x10] — changed-field index array (int*)
        uint p9)   // [rsp+0x18]
    {
        var n = Interlocked.Increment(ref _count);
        if (n <= 30 && _logger is { } log)
        {
            try
            {
                var tid    = Environment.CurrentManagedThreadId;
                var client = RecipientCapture.CurrentClient;

                // Attempt to read the first field name from the serializer's field array.
                // CFlattenedSerializer layout (confirmed in FlattenedSerializerLayout.cs):
                //   +0x00 = name ptr, +0x08 = field count (int), +0x10 = field-array ptr.
                // Field array: stride 8; each entry is a ptr to a field record.
                // Field record: +0x08 = char* m_pszFieldName (confirmed by SerializerProbe).
                var fieldInfo = "";
                var serName   = "";
                try
                {
                    if (IsUserPtr(a))
                    {
                        var sname = *(nint*) (a + 0x00);
                        if (IsUserPtr(sname))
                            serName = ReadShortAscii(sname, 32);

                        var fieldCount = *(int*) (a + 0x08);
                        var fieldArr   = *(nint*) (a + 0x10);
                        if (fieldCount is > 0 and < 4096 && IsUserPtr(fieldArr))
                        {
                            // Read first field name as a quick identity probe.
                            var rec0 = *(nint*) fieldArr;
                            if (IsUserPtr(rec0))
                            {
                                var np = *(nint*) (rec0 + 0x08);
                                if (IsUserPtr(np))
                                    fieldInfo = $"field[0]=\"{ReadShortAscii(np, 24)}\" cnt={fieldCount}";
                            }
                        }
                    }
                }
                catch { /* read is best-effort, never abort passthrough */ }

                // Read changed-field count from param_8: *(int*)p8 = count, *(int*)(p8+4) = first id.
                var changedCount = -1;
                try
                {
                    if (IsUserPtr(p8))
                        changedCount = *(int*) p8;
                }
                catch { /* best-effort */ }

                log.LogInformation(
                    "WFL#{N} tid={Tid} client=0x{Client:X} ser=\"{Ser}\" {Fields} changed={Changed} dst_bfw=0x{Dst:X}",
                    n, tid, client, serName, fieldInfo, changedCount, d);
            }
            catch { /* never let logging break the passthrough */ }
        }

        // Pure passthrough — all 9 args relayed unchanged.
        return ((delegate* unmanaged[Cdecl]<
            nint, nint, nint, nint, nint, uint,
            uint, nint, uint,
            nint>) _trampoline)(a, b, c, d, e, p6, p7, p8, p9);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string ReadShortAscii(nint p, int maxLen)
    {
        if (!IsUserPtr(p)) return string.Empty;
        try
        {
            var buf = stackalloc byte[maxLen + 1];
            int len = 0;
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
}
