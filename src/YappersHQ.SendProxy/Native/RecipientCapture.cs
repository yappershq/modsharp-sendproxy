using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Detour on CNetworkGameServer::PerClientEncode (engine2, file-RVA 0x8fae40).
///     ABI: rdi=CNetworkGameServer*, rsi=CServerSideClient*.
///     Captures rsi into <see cref="CurrentClient"/> [ThreadStatic] before calling the trampoline,
///     clears it after — so CurrentClient is non-zero only while this client's encode chain executes
///     on this thread (WriteDeltaEntity_Internal, EncodeInt32, etc. run synchronously inside).
///     Phase-2 value-encode hooks read CurrentClient to key per-recipient spoofs.
///     Also logs the first 30 calls (tid + client ptr) for verification.
/// </summary>
internal static unsafe class RecipientCapture
{
    [ThreadStatic]
    private static nint _currentClient;

    /// <summary>CServerSideClient* for the client being encoded on this thread. Zero on all other threads.</summary>
    public static nint CurrentClient => _currentClient;

    private static IDetourHook? _hook;
    private static nint         _trampoline;
    private static ILogger?     _logger;
    private static int          _count;

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

    // 6-arg passthrough (rdi=server, rsi=client, rdx..r9=c..f — 6 args for ABI correctness).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint b, nint c, nint d, nint e, nint f)
    {
        _currentClient = b;
        nint result;
        try
        {
            var n = Interlocked.Increment(ref _count);
            if (n <= 30 && _logger is { } log)
            {
                try
                {
                    log.LogInformation("RECIP#{N} tid={Tid} server=0x{A:X} client=0x{B:X}",
                        n, Environment.CurrentManagedThreadId, a, b);
                }
                catch { }
            }

            result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>) _trampoline)(a, b, c, d, e, f);
        }
        finally
        {
            _currentClient = 0;
        }
        return result;
    }
}
