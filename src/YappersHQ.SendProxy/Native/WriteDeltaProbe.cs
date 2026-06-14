using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     READ-ONLY probe detour on CNetworkGameServerBase::WriteDeltaEntity_Internal (engine2).
///     Purpose: confirm that this function runs serially per-client on the send thread (NOT inside
///     parallel pack workers), and reveal the live argument layout for Phase-2 per-client spoofing.
///
///     ABI (SysV x86-64): rdi=a (this/CNetworkGameServerBase*), rsi=b (delta context*).
///     Known fields in the delta context (from /tmp/wde.out RE):
///       *(int*)(b+0x34)  = entityIndex
///       *(nint*)(b+0x88) = bf_write*
///       *(nint*)(b+0x90) = from-snapshot*
///       *(nint*)(b+0x98) = to-snapshot*
///
///     The hook is a pure passthrough — it never modifies any argument or return value.
///     Logs the first 40 calls with managed thread ID, then stops to avoid log flood.
/// </summary>
internal static unsafe class WriteDeltaProbe
{
    private static IDetourHook? _hook;
    private static nint _trampoline;
    private static ILogger? _logger;
    private static int _count;

    // Cheap user-space range gate: valid Linux user pointers have bits [63:48] == 0,
    // and the 7th byte == 0x7F is a reliable heap/stack heuristic.
    private static bool IsUserPtr(nint p) => p > 0 && ((ulong) p >> 40) == 0x7F;

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
        logger.LogInformation("WriteDeltaProbe installed @ 0x{Addr:X} (trampoline=0x{Tr:X})",
            wdeAddr, _trampoline);
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

    // 6-arg passthrough: rdi=a (this), rsi=b (delta ctx), rdx=c, rcx=d, r8=e, r9=f.
    // WDE_Internal only uses rdi+rsi, but we declare all 6 so the trampoline call is
    // bit-identical to any 6-register SysV call (no stack args to worry about).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        var n = Interlocked.Increment(ref _count);
        if (n <= 40 && _logger is { } log)
        {
            try
            {
                var threadId = Environment.CurrentManagedThreadId;

                int entityIndex = -1;
                nint bfWrite = 0;
                nint fromSnap = 0;
                nint toSnap = 0;

                if (IsUserPtr(b))
                {
                    try { entityIndex = *(int*) (b + 0x34); } catch { /* guard */ }
                    try { bfWrite   = *(nint*) (b + 0x88); } catch { /* guard */ }
                    try { fromSnap  = *(nint*) (b + 0x90); } catch { /* guard */ }
                    try { toSnap    = *(nint*) (b + 0x98); } catch { /* guard */ }
                }

                log.LogInformation(
                    "WDE#{N} tid={Tid} this=0x{A:X} ctx=0x{B:X} ent={Ent} bfw=0x{Bfw:X} from=0x{From:X} to=0x{To:X}",
                    n, threadId, a, b, entityIndex, bfWrite, fromSnap, toSnap);
            }
            catch { /* never let logging break the passthrough */ }
        }

        return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e, f);
    }
}
