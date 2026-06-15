using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     READ-ONLY probe detour on CNetworkGameServerBase::WriteDeltaEntity_Internal.
///     ABI: rdi=this (CNetworkGameServerBase*), rsi=delta ctx*.
///     Known ctx fields (from /tmp/wde.out RE):
///       *(int*)(ctx+0x34)  = entityIndex
///       *(nint*)(ctx+0x88) = bf_write*
///       *(nint*)(ctx+0x90) = from-snapshot*
///       *(nint*)(ctx+0x98) = to-snapshot*
///     Logs the first 40 calls (tid + ctx fields) then stops. Pure passthrough.
/// </summary>
internal static unsafe class WriteDeltaProbe
{
    private static IDetourHook? _hook;
    private static nint _trampoline;
    private static ILogger? _logger;
    private static int _count;

    public static bool Install(InterfaceBridge bridge, ILogger logger, nint wdeAddr)
    {
        if (_hook is not null)
        {
            logger.LogInformation("WriteDeltaProbe: already installed");
            return true;
        }
        if (wdeAddr == 0)
        {
            logger.LogWarning("WriteDeltaProbe: null target address — cannot install");
            return false;
        }

        _logger = logger;
        _count = 0;

        var hook = bridge.HookManager.CreateDetourHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) &Hook;
        hook.Prepare(wdeAddr, hookFn);
        if (!hook.Install())
        {
            logger.LogWarning("WriteDeltaProbe: IDetourHook.Install() failed");
            return false;
        }

        _hook = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("WriteDeltaProbe installed @ 0x{Addr:X} (trampoline=0x{Tr:X})", wdeAddr, _trampoline);
        return true;
    }

    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook?.Dispose();
        _hook = null;
        _trampoline = 0;
        _logger?.LogInformation("WriteDeltaProbe uninstalled");
        _logger = null;
    }

    // 6-arg passthrough: rdi=this, rsi=ctx, rdx..r9=c..f.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        var n = Interlocked.Increment(ref _count);
        if (n <= 40 && _logger is { } log)
        {
            try
            {
                int entityIndex = -1;
                nint bfWrite = 0, fromSnap = 0, toSnap = 0;

                if (NativeUtil.IsUserPtr(b))
                {
                    try { entityIndex = *(int*)  (b + 0x34); } catch { }
                    try { bfWrite    = *(nint*) (b + 0x88); } catch { }
                    try { fromSnap   = *(nint*) (b + 0x90); } catch { }
                    try { toSnap     = *(nint*) (b + 0x98); } catch { }
                }

                log.LogInformation(
                    "WDE#{N} tid={Tid} this=0x{A:X} ctx=0x{B:X} ent={Ent} bfw=0x{Bfw:X} from=0x{From:X} to=0x{To:X}",
                    n, Environment.CurrentManagedThreadId, a, b, entityIndex, bfWrite, fromSnap, toSnap);
            }
            catch { }
        }

        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e, f);
    }
}
