using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     VERIFY-MODE (read-only) probe on CFlattenedSerializer::WriteFieldList
///     (libnetworksystem.so, file-RVA 0x343b60).
///
///     WFL is the per-client encode loop: for each changed field it calls GetBitRange (field identity)
///     then the bit-copy primitive (FUN_00500b70) to copy bits into the per-client bf_write.
///     This probe confirms that RecipientCapture.CurrentClient is visible inside WFL and that
///     serializer/field identity is readable from param_1.
///
///     ABI (SysV x86-64) — 9 parameters, 3 on the stack:
///       rdi=param_1  (CFlattenedSerializer*): name@+0x00, field-count@+0x08, field-array@+0x10
///       rsi=param_2  (context qword)
///       rdx=param_3  (encoder-block ptr)
///       rcx=param_4  (dst bf_write* for this client)
///       r8 =param_5  (src pack-buffer descriptor)
///       r9 =param_6  (entity class index / delta tick)
///       [rsp+0x08]=param_7, [rsp+0x10]=param_8 (changed-field index array), [rsp+0x18]=param_9
///
///     All 9 args are relayed unchanged (pure passthrough). The full 9-arg signature is required —
///     using only 6 args caused the same crash as the EncodeField-entry probe (stack args read garbage).
///
///     Sig (nosoop makesig, 24 bytes, MATCHES:1 in libnetworksystem, build 2026-06-02):
///       55 48 8D 05 ? ? ? ? 45 31 DB 66 0F EF C0 48 89 E5 41 57 41 56 4C 8D
/// </summary>
internal static unsafe class WriteFieldProbe
{
    private static IDetourHook? _hook;
    private static nint         _trampoline;
    private static ILogger?     _logger;
    private static int          _count;

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
        // 9 args: 6 register + 3 stack. Declaring all 9 ensures the managed→unmanaged call emits
        // the correct SysV frame with 3 stack pushes for param_7..param_9.
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<
            nint, nint, nint, nint, nint, uint,
            uint, nint, uint,
            nint>) &Hook;
        hook.Prepare(wflAddr, hookFn);
        if (!hook.Install())
        {
            logger.LogWarning("WriteFieldProbe: IDetourHook.Install() failed");
            return false;
        }

        _hook       = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("WriteFieldProbe installed @ 0x{Addr:X} (trampoline=0x{Tr:X})", wflAddr, _trampoline);
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

    // ABI: rdi=a (CFlattenedSerializer*), rsi=b, rdx=c, rcx=d (dst bf_write*),
    //      r8=e (src pack-buf), r9=p6, stack=[p7, p8 (changed-field array), p9].
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(
        nint a, nint b, nint c, nint d, nint e,
        uint p6, uint p7, nint p8, uint p9)
    {
        var n = Interlocked.Increment(ref _count);
        if (n <= 30 && _logger is { } log)
        {
            try
            {
                var tid    = Environment.CurrentManagedThreadId;
                var client = RecipientCapture.CurrentClient;

                // Read serializer name + first field name from param_1.
                // CFlattenedSerializer: +0x00=name ptr, +0x08=field count, +0x10=field-array ptr.
                // Field record: +0x08=char* m_pszFieldName (confirmed by SerializerProbe).
                var serName   = string.Empty;
                var fieldInfo = string.Empty;
                try
                {
                    if (NativeUtil.IsUserPtr(a))
                    {
                        serName = NativeUtil.ReadShortAscii(*(nint*) (a + 0x00), 32);

                        var fieldCount = *(int*) (a + 0x08);
                        var fieldArr   = *(nint*) (a + 0x10);
                        if (fieldCount is > 0 and < 4096 && NativeUtil.IsUserPtr(fieldArr))
                        {
                            var rec0 = *(nint*) fieldArr;
                            if (NativeUtil.IsUserPtr(rec0))
                            {
                                var f0name = NativeUtil.ReadShortAscii(*(nint*) (rec0 + 0x08), 24);
                                fieldInfo = $"field[0]=\"{f0name}\" cnt={fieldCount}";
                            }
                        }
                    }
                }
                catch { }

                var changedCount = -1;
                try { if (NativeUtil.IsUserPtr(p8)) changedCount = *(int*) p8; }
                catch { }

                log.LogInformation(
                    "WFL#{N} tid={Tid} client=0x{Client:X} ser=\"{Ser}\" {Fields} changed={Changed} dst_bfw=0x{Dst:X}",
                    n, tid, client, serName, fieldInfo, changedCount, d);
            }
            catch { }
        }

        return ((delegate* unmanaged[Cdecl]<
            nint, nint, nint, nint, nint, uint,
            uint, nint, uint,
            nint>) _trampoline)(a, b, c, d, e, p6, p7, p8, p9);
    }
}
