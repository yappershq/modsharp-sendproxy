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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;

namespace YappersHQ.SendProxy.Native;

/// <summary>
///     Detour on <c>CFlattenedSerializer::Encode</c> — the per-entity encode that builds the shared snapshot
///     during <c>PackEntities</c>. The per-field leaf encoders fire synchronously within its call tree, so
///     stashing the serializer + entity index here (thread-local) gives the encoder hook the context it needs
///     to fire a per-(entity, field) proxy callback ONCE, off the per-client fan-out.
///     <para>
///         SysV signature (RE'd @ libnetworksystem 0x33afe0): args rdi, <b>rsi = CFlattenedSerializer*</b>,
///         rdx, rcx, <b>r8d = entity index</b> (the value formatted into the "Encode failure for entity %d"
///         log). Captured into [ThreadStatic] for the duration of the call; cleared on exit.
///     </para>
///     <para>
///         PackEntities is parallel across entities, but a single entity is encoded on ONE thread, so the
///         thread-local is correct per encoding thread and never races.
///     </para>
/// </summary>
internal static unsafe class EncodeCapture
{
    [ThreadStatic]
    private static nint _serializer;

    [ThreadStatic]
    private static int _entityIndex;

    [ThreadStatic]
    private static bool _active;

    /// <summary>CFlattenedSerializer* of the entity being encoded on this thread (0 if not in an encode).</summary>
    public static nint Serializer => _active ? _serializer : 0;

    /// <summary>Entity index being encoded on this thread (-1 if not in an encode).</summary>
    public static int EntityIndex => _active ? _entityIndex : -1;

    /// <summary>True while this thread is inside CFlattenedSerializer::Encode.</summary>
    public static bool Active => _active;

    public static nint Addr;

    private static IDetourHook? _hook;
    private static nint         _trampoline;
    private static ILogger?     _logger;

    public static bool Install(InterfaceBridge bridge, ILogger logger)
    {
        if (_hook is not null)
        {
            return true;
        }

        if (Addr == 0)
        {
            logger.LogWarning("EncodeCapture: CFlattenedSerializer::Encode address not resolved — entity context unavailable");

            return false;
        }

        _logger = logger;

        var hook   = bridge.HookManager.CreateDetourHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, nint>) &Hook;
        hook.Prepare(Addr, hookFn);

        if (!hook.Install())
        {
            logger.LogWarning("EncodeCapture: IDetourHook.Install() failed @ 0x{Addr:X}", Addr);

            return false;
        }

        _hook       = hook;
        _trampoline = hook.Trampoline;
        logger.LogInformation("EncodeCapture installed @ 0x{Addr:X}", Addr);

        return true;
    }

    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook?.Dispose();
        _hook       = null;
        _trampoline = 0;
        _logger?.LogInformation("EncodeCapture uninstalled");
        _logger = null;
    }

    // args: rdi(a), rsi(serializer), rdx(c), rcx(d), r8d(entityIndex). Cdecl marshalling maps the first five
    // integer args positionally — entityIndex arrives as the 5th (uint to match r8d; cast to int).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Hook(nint a, nint serializer, nint c, nint d, uint entityIndex)
    {
        var prevSer    = _serializer;
        var prevIdx    = _entityIndex;
        var prevActive = _active;

        _serializer  = serializer;
        _entityIndex = (int) entityIndex;
        _active      = true;

        try
        {
            return ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, nint>) _trampoline)(a, serializer, c, d, entityIndex);
        }
        finally
        {
            // Restore (encode calls can nest for embedded serializers) rather than blindly clearing.
            _serializer  = prevSer;
            _entityIndex = prevIdx;
            _active      = prevActive;
        }
    }
}
