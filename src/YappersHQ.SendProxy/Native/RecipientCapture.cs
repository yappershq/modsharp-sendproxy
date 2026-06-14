using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     READ-ONLY probe detour on CNetworkGameServer::PerClientEncode (engine2, file-RVA 0x8fae40).
///
///     ABI (SysV x86-64): rdi=a (this/CNetworkGameServer*), rsi=b (CServerSideClient*).
///     The hook captures rsi into a [ThreadStatic] before calling the trampoline, then clears it
///     after — so <see cref="CurrentClient"/> is non-zero only while the original function (and
///     everything it calls, including WriteDeltaEntity_Internal) executes on this thread.
///
///     Phase-2 usage: value-encode hooks running on the same engine worker thread read
///     <see cref="CurrentClient"/> to key per-recipient spoofs without passing the client ptr
///     through every intermediate call frame.
///
///     The probe also logs the first 30 calls (managed thread id + client ptr) so we can confirm:
///     one call = one client, calls come from worker threads, and the ptr is stable/non-null.
///
///     Pure passthrough — no argument or return value is ever modified.
/// </summary>
internal static unsafe class RecipientCapture
{
    // ── Per-thread client ptr ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The CServerSideClient* for the client currently being encoded on this thread.
    ///     Non-zero only while inside a PerClientEncode call (set before trampoline, cleared after).
    ///     Zero on threads that are not executing PerClientEncode (main thread, pack workers, etc.).
    /// </summary>
    [ThreadStatic]
    private static nint _currentClient;

    /// <summary>Public read-only accessor for Phase-2 encoder hooks.</summary>
    public static nint CurrentClient => _currentClient;

    // ── Lifecycle state ───────────────────────────────────────────────────────────────────────

    private static IDetourHook? _hook;
    private static nint         _trampoline;
    private static ILogger?     _logger;
    private static int          _count;      // log throttle

    // Cheap user-space range gate: valid Linux user pointers have bits [63:48] == 0,
    // and the 7th byte == 0x7F is a reliable heap/stack heuristic.
    private static bool IsUserPtr(nint p) => p > 0 && ((ulong) p >> 40) == 0x7F;

    // ── Install / Uninstall ───────────────────────────────────────────────────────────────────

    public static bool Install(InterfaceBridge bridge, ILogger logger, nint perClientEncodeAddr)
    {
        if (_hook is not null)
        {
            logger.LogInformation("RecipientCapture: already installed");
            return true;
        }
        if (perClientEncodeAddr == 0)
        {
            logger.LogWarning("RecipientCapture: null target address — cannot install");
            return false;
        }

        _logger = logger;
        _count  = 0;

        var hook   = bridge.HookManager.CreateDetourHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) &Hook;
        hook.Prepare(perClientEncodeAddr, hookFn);
        if (!hook.Install())
        {
            logger.LogWarning("RecipientCapture: IDetourHook.Install() failed");
            return false;
        }

        _hook       = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("RecipientCapture installed @ 0x{Addr:X} (trampoline=0x{Tr:X})",
            perClientEncodeAddr, _trampoline);
        return true;
    }

    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook?.Dispose();
        _hook       = null;
        _trampoline = 0;
        _logger?.LogInformation("RecipientCapture uninstalled");
        _logger = null;
    }

    // ── Hook ─────────────────────────────────────────────────────────────────────────────────

    // 6-arg passthrough: rdi=a (CNetworkGameServer*), rsi=b (CServerSideClient*), rdx..r9=c..f.
    // Only rdi+rsi are documented; declare all 6 so the trampoline frame is bit-identical to any
    // 6-register SysV call and no stack arguments need re-staging.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        // Capture rsi (CServerSideClient*) into the thread-local BEFORE calling the trampoline.
        // Everything downstream on this thread (WriteDeltaEntity_Internal, EncodeInt32, etc.)
        // runs synchronously inside the trampoline, so _currentClient is valid for their duration.
        _currentClient = b;
        nint result;
        try
        {
            // Probe logging — first 30 calls only, gated to avoid log flood in production.
            var n = Interlocked.Increment(ref _count);
            if (n <= 30 && _logger is { } log)
            {
                try
                {
                    var tid = Environment.CurrentManagedThreadId;
                    log.LogInformation(
                        "RECIP#{N} tid={Tid} server=0x{A:X} client=0x{B:X}",
                        n, tid, a, b);
                }
                catch { /* logging must never break the passthrough */ }
            }

            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e, f);
        }
        finally
        {
            // Always clear — even if the trampoline threw — so the thread-local doesn't leak
            // a stale client ptr to code running after this function returns.
            _currentClient = 0;
        }
        return result;
    }
}
