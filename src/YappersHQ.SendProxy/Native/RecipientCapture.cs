/*
 * SendProxy for ModSharp (CS2)
 * Copyright (C) 2026 YappersHQ. All Rights Reserved.
 *
 * This file is part of SendProxy for ModSharp.
 * SendProxy is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 *
 * SendProxy is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with SendProxy. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Detour on CNetworkGameServer::PerClientEncode (rsi = CServerSideClient*). Captures the recipient
///     into a [ThreadStatic] for the duration of that client's encode chain on this thread, so the
///     downstream substitution hooks can key per-recipient spoofs. Cleared on exit.
/// </summary>
internal static unsafe class RecipientCapture
{
    [ThreadStatic]
    private static nint _currentClient;

    /// <summary>CServerSideClient* for the client being encoded on this thread; zero on all other threads.</summary>
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
                catch
                {
                    // Diagnostics only — never let logging faults escape the hook.
                }
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
