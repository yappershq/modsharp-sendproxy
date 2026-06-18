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
///     <b>MID-FUNCTION hook</b> on the per-entity encode wrapper <c>EncodeEntity</c> @0x38a130 (the single
///     caller of <c>CFlattenedSerializer::Encode</c>) during the shared <c>PackEntities</c> pass. Captures the
///     entity index + serializer into a [ThreadStatic] so the per-field encoder hook can fire a per-(entity,
///     field) proxy callback ONCE with the right entity context.
///     <para>
///         <b>Why a midhook, not a detour:</b> the encode path is RECURSIVE (Encode re-enters itself for
///         nested serializers; 0x38a130 can be re-entered too). An <see cref="IDetourHook"/> calls the
///         trampoline, so its managed frame WRAPS the entire recursive execution → frame × depth → stack
///         overflow (observed live on both Encode and 0x38a130). An <see cref="IMidFuncHook"/> instead just
///         OBSERVES the registers at the entry and resumes the original (no trampoline call, no wrapping
///         frame) — the callback returns immediately, so frames never accumulate across the recursion. This
///         is the same mechanism the GetBitRange field-path midhook uses in production. Verified against
///         ModSharp's IMidFuncHook contract. See docs/ENCODE_CALLGRAPH_RE.md.
///     </para>
///     <para>
///         The serializer is <c>*(arg2 + 0x8)</c> and the entity index is the 5th argument on both platforms;
///         only the ABI slot differs. SysV (linux @0x38a130): arg2 = <c>rsi</c>, entity index = <c>r8d</c>
///         (the value formatted into the "...for entity %d" log). Win64 (@0x1800a5160): arg2 = <c>rdx</c>,
///         and the 5th arg lands on the stack at <c>[rsp + 0x28]</c> at the entry instruction. Read at entry,
///         valid for that entity's encode (the leaf encoders fire after this on the same thread). PackEntities
///         is parallel across entities, so capture is per-thread.
///     </para>
/// </summary>
internal static unsafe class EncodeCapture
{
    [ThreadStatic]
    private static nint _serializer;

    [ThreadStatic]
    private static int _entityIndex;

    [ThreadStatic]
    private static bool _captured;

    [ThreadStatic]
    private static string? _serializerName;

    [ThreadStatic]
    private static bool _serializerHasProxy;

    /// <summary>CFlattenedSerializer* of the entity being encoded on this thread (0 if none captured).</summary>
    public static nint Serializer => _captured ? _serializer : 0;

    /// <summary>Resolved serializer name of the entity being encoded (empty if none).</summary>
    public static string SerializerName => _captured ? _serializerName ?? string.Empty : string.Empty;

    /// <summary>
    ///     Fast-path gate: true only when the current entity's serializer has at least one registered proxy.
    ///     Resolved ONCE here (per entity) so the per-field encoder hook can skip all per-field work for the
    ///     overwhelming majority of entities that no proxy touches.
    /// </summary>
    public static bool SerializerHasProxy => _captured && _serializerHasProxy;

    /// <summary>Entity index being encoded on this thread (-1 if none captured).</summary>
    public static int EntityIndex => _captured ? _entityIndex : -1;

    /// <summary>True once this thread has captured an entity (a per-field encode is in scope).</summary>
    public static bool Active => _captured;

    public static nint Addr;

    private static IMidFuncHook? _hook;
    private static ILogger?      _logger;

    public static bool Install(InterfaceBridge bridge, ILogger logger)
    {
        if (_hook is not null)
        {
            return true;
        }

        if (Addr == 0)
        {
            logger.LogWarning("EncodeCapture: EncodeEntity address not resolved — entity context unavailable");

            return false;
        }

        _logger = logger;

        var hook   = bridge.HookManager.CreateMidFuncHook();
        var hookFn = (nint) (delegate* unmanaged[Cdecl]<MidHookContext*, void>) &Hook;
        hook.Prepare(Addr, hookFn);

        if (!hook.Install())
        {
            logger.LogWarning("EncodeCapture: IMidFuncHook.Install() failed @ 0x{Addr:X}", Addr);

            return false;
        }

        _hook = hook;
        logger.LogInformation("EncodeCapture midhook installed @ 0x{Addr:X}", Addr);

        return true;
    }

    public static void Uninstall()
    {
        _hook?.Uninstall();
        _hook?.Dispose();
        _hook = null;
        _logger?.LogInformation("EncodeCapture uninstalled");
        _logger = null;
    }

    // Mid-function hook: OBSERVE the entry registers, then PolyHook resumes the original (no trampoline call,
    // no wrapping frame — safe on the recursive encode path). r8 = entity index (low 32 bits), rsi = arg2 →
    // serializer at *(rsi+0x8). Every deref is IsUserPtr-guarded; never throws into engine code.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Hook(MidHookContext* ctx)
    {
        // The serializer is *(arg2 + 8) and the entity index is the 5th arg on BOTH platforms; only the ABI
        // slot differs. SysV: arg2 = rsi, arg5 = r8d. Win64: arg2 = rdx, arg5 is on the stack at [rsp+0x28]
        // (rsp points at the return address at the hook's entry instruction). See gamedata EncodeEntity note.
        nint arg2;
        int  entIdx;
        if (OperatingSystem.IsWindows())
        {
            arg2   = ctx->rdx;
            var sp = ctx->rsp;
            entIdx = NativeUtil.IsUserPtr(sp) ? *(int*) (sp + 0x28) : -1;
        }
        else
        {
            arg2   = ctx->rsi;
            entIdx = (int) ctx->r8;
        }

        var ser = NativeUtil.IsUserPtr(arg2) ? *(nint*) (arg2 + 0x8) : 0;

        _serializer  = ser;
        _entityIndex = entIdx;

        // Resolve the serializer name + whether any proxy targets it ONCE here, so the per-field encoder hook
        // can skip everything for entities no proxy touches. Cheap: once per entity, not per field.
        if (!ProxyRegistry.IsEmpty && NativeUtil.IsUserPtr(ser))
        {
            _serializerName     = NativeUtil.ReadShortAscii(*(nint*) ser, 48);
            _serializerHasProxy = _serializerName.Length > 0 && ProxyRegistry.HasSerializer(_serializerName);
        }
        else
        {
            _serializerName     = null;
            _serializerHasProxy = false;
        }

        _captured = true;
    }
}
