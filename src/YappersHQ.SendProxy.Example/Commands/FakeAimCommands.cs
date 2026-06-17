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
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.TargetingManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Example.Commands;

internal sealed class FakeAimCommands : ISpCommandCategory
{
    private readonly ExampleContext _ctx;

    // sp_fakeaim session state: a per-client (issuer-only) HP/team/cycling-color spoof on resolved targets.
    private nint               _aimIssuer;            // recipient client ptr that sees the fakes
    private readonly List<int> _aimPawns       = new(); // target pawn entity indices (color re-dirtied each tick)
    private readonly List<int> _aimControllers = new();
    private volatile uint      _aimColor;             // current cycling render color (RGBA), read on send threads
    private int                _aimHue;
    private Guid               _aimTimer;

    public FakeAimCommands(ExampleContext ctx) => _ctx = ctx;

    public void Register(IAdminCommandRegistry registry)
    {
        registry.RegisterAdminCommand("sp_fakeaim",     OnFakeAim,    [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_fakeaim_off", OnFakeAimOff, [ExampleContext.Permission]);
    }

    public void Unregister()
    {
        if (_ctx.SendProxy is { } sp)
        {
            ClearFakeAim(sp);
        }
        else if (_aimTimer != Guid.Empty)
        {
            _ctx.ModSharp.StopTimer(_aimTimer);
            _aimTimer = Guid.Empty;
        }
    }

    // sp_fakeaim <target> — resolve a target string via TargetingManager (@aim, a nick, @t, multiple…) and
    // make each resolved player appear, ONLY to the issuer, with fake HP (1) + team (CT) + a continuously
    // cycling render colour. Pure per-client + per-entity: every other client still sees the real values.
    // A repeating timer advances the colour and re-dirties m_clrRender on the targets so it keeps changing.
    private void OnFakeAim(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp || issuer is null)
        {
            return;
        }

        if (_ctx.Targeting?.Instance is not { } targeting)
        {
            _ctx.Reply(issuer, "sp_fakeaim: TargetingManager not available");

            return;
        }

        var targetArg = command.ArgCount >= 1 ? command.GetArg(1) : PredefinedTargets.Aim;

        // Clear any previous session so hooks/timer don't stack.
        ClearFakeAim(sp);

        var targets = targeting.GetByTarget(issuer, targetArg).ToList();
        if (targets.Count == 0)
        {
            _ctx.Reply(issuer, $"sp_fakeaim: no targets matched '{targetArg}'");

            return;
        }

        _aimIssuer = issuer.GetAbsPtr();
        var issuerPtr = _aimIssuer;
        _aimHue   = 0;
        _aimColor = 0xFF0000FFu; // opaque red to start (RGBA)

        foreach (var target in targets)
        {
            if (target.GetPlayerController() is not { } ctrl)
            {
                continue;
            }

            _aimControllers.Add((int) ctrl.Index);
            sp.Hook(ctrl, "CCSPlayerController", "m_iPawnHealth", (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = 1; return true; });
            sp.Hook(ctrl, "CCSPlayerController", "m_iTeamNum",    (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = 3; return true; });
            SafeDirty(ctrl, "m_iPawnHealth");   // force an initial re-send so it shows at once
            SafeDirty(ctrl, "m_iTeamNum");

            if (ctrl.GetPlayerPawn() is { } pawn)
            {
                _aimPawns.Add((int) pawn.Index);
                sp.Hook(pawn, "CCSPlayerPawn", "m_iHealth",          (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = 1; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_iTeamNum",         (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = 3; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_clrRender",        (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = unchecked((int) _aimColor); return true; });
                // Glow: a coloured outline. The fields live on the EMBEDDED CGlowProperty (m_Glow), not on
                // the pawn class — so the per-client hook matches by leaf name but the force-dirty goes
                // through the parent m_Glow + sub-offset (see ReDirtyPawn). Full enable set (mirrors the
                // known glow recipe: type=3, team=-1 all-see, wide range): type/team/range/rangemin/colour.
                sp.Hook(pawn, "CCSPlayerPawn", "m_iGlowType",         (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = 3; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_iGlowTeam",         (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = -1; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_nGlowRange",        (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = 99999; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_nGlowRangeMin",     (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = 0; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_glowColorOverride", (IGameClient c, IBaseEntity _, ref SpoofValue v) => { if (c.GetAbsPtr() != issuerPtr) return false; v.AsInt = unchecked((int) _aimColor); return true; });
                ReDirtyPawn(pawn);
            }
        }

        // Drive the colour cycle: every 100ms advance the hue and re-dirty the colour fields on each target
        // so the engine re-sends them (the per-client hook then writes the new colour). Repeatable timer.
        _aimTimer = _ctx.ModSharp.PushTimer(CycleFakeAim, 0.1, GameTimerFlags.Repeatable);

        _ctx.Reply(issuer, $"sp_fakeaim: faking HP/team/colour/glow on {targets.Count} target(s) — only YOU see it. sp_fakeaim_off to clear.");
    }

    private void OnFakeAimOff(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is { } sp)
        {
            ClearFakeAim(sp);
        }

        _ctx.Reply(issuer, "sp_fakeaim_off: cleared (real values re-sent)");
    }

    // CGlowProperty sub-field offsets within the embedded m_Glow. NOT hardcoded — resolved from the live
    // schema at first use (offsets shift across CS2 updates, so reading them at runtime is recompile-proof).
    // These are offsets WITHIN CGlowProperty; the force-dirty applies them as extraOffset on top of m_Glow.
    private ushort _glowTypeOffset, _glowTeamOffset, _glowRangeOffset, _glowRangeMinOffset, _glowColorOffset;
    private bool   _glowOffsetsResolved;

    private void EnsureGlowOffsets()
    {
        if (_glowOffsetsResolved)
        {
            return;
        }

        _glowTypeOffset     = (ushort) _ctx.Schema.GetNetVarOffset("CGlowProperty", "m_iGlowType");
        _glowTeamOffset     = (ushort) _ctx.Schema.GetNetVarOffset("CGlowProperty", "m_iGlowTeam");
        _glowRangeOffset    = (ushort) _ctx.Schema.GetNetVarOffset("CGlowProperty", "m_nGlowRange");
        _glowRangeMinOffset = (ushort) _ctx.Schema.GetNetVarOffset("CGlowProperty", "m_nGlowRangeMin");
        _glowColorOffset    = (ushort) _ctx.Schema.GetNetVarOffset("CGlowProperty", "m_glowColorOverride");
        _glowOffsetsResolved = true;
    }

    // Force a field to re-transmit without changing its real value, guarded so a name that isn't a
    // netvar on this class can never abort the command (the embedded glow fields are the reason —
    // they're not direct pawn netvars).
    private void SafeDirty(IBaseEntity entity, string field, ushort extraOffset = 0)
    {
        try
        {
            entity.NetworkStateChanged(field, false, extraOffset);
        }
        catch (Exception e)
        {
            _ctx.Logger.LogWarning("sp_fakeaim: NetworkStateChanged({Field},+{Off}) failed: {Msg}", field, extraOffset, e.Message);
        }
    }

    // Re-dirty all spoofed fields on a target pawn so they re-transmit at once. Glow lives on the
    // embedded m_Glow (CGlowProperty), so it's dirtied through the parent field + the sub-field offset.
    private void ReDirtyPawn(IBaseEntity pawn)
    {
        EnsureGlowOffsets();
        SafeDirty(pawn, "m_iHealth");
        SafeDirty(pawn, "m_iTeamNum");
        SafeDirty(pawn, "m_clrRender");
        SafeDirty(pawn, "m_Glow", _glowTypeOffset);
        SafeDirty(pawn, "m_Glow", _glowTeamOffset);
        SafeDirty(pawn, "m_Glow", _glowRangeOffset);
        SafeDirty(pawn, "m_Glow", _glowRangeMinOffset);
        SafeDirty(pawn, "m_Glow", _glowColorOffset);
    }

    // Timer tick: advance the cycling colour and re-dirty the colour/glow on every target so they
    // re-transmit (the per-client hooks then write the new colour).
    private void CycleFakeAim()
    {
        _aimHue   = (_aimHue + 15) % 360;
        _aimColor = HueToRgba(_aimHue);

        foreach (var pidx in _aimPawns)
        {
            if (_ctx.EntityManager.FindEntityByIndex((EntityIndex) pidx) is { } pawn)
            {
                SafeDirty(pawn, "m_clrRender");
                SafeDirty(pawn, "m_Glow", _glowColorOffset);
            }
        }
    }

    // Tear down a fakeaim session: stop the timer, unhook every per-entity registration on the targets, then
    // re-dirty so the real values go back out, and clear state.
    private void ClearFakeAim(ISendProxyManager sp)
    {
        if (_aimTimer != Guid.Empty)
        {
            _ctx.ModSharp.StopTimer(_aimTimer);
            _aimTimer = Guid.Empty;
        }

        foreach (var cidx in _aimControllers)
        {
            if (_ctx.EntityManager.FindEntityByIndex((EntityIndex) cidx) is not { } ctrl)
            {
                continue;
            }

            sp.Unhook(ctrl, "CCSPlayerController", "m_iPawnHealth");
            sp.Unhook(ctrl, "CCSPlayerController", "m_iTeamNum");
            SafeDirty(ctrl, "m_iPawnHealth");
            SafeDirty(ctrl, "m_iTeamNum");
        }

        foreach (var pidx in _aimPawns)
        {
            if (_ctx.EntityManager.FindEntityByIndex((EntityIndex) pidx) is not { } pawn)
            {
                continue;
            }

            sp.Unhook(pawn, "CCSPlayerPawn", "m_iHealth");
            sp.Unhook(pawn, "CCSPlayerPawn", "m_iTeamNum");
            sp.Unhook(pawn, "CCSPlayerPawn", "m_clrRender");
            sp.Unhook(pawn, "CCSPlayerPawn", "m_iGlowType");
            sp.Unhook(pawn, "CCSPlayerPawn", "m_iGlowTeam");
            sp.Unhook(pawn, "CCSPlayerPawn", "m_nGlowRange");
            sp.Unhook(pawn, "CCSPlayerPawn", "m_nGlowRangeMin");
            sp.Unhook(pawn, "CCSPlayerPawn", "m_glowColorOverride");
            ReDirtyPawn(pawn);
        }

        _aimControllers.Clear();
        _aimPawns.Clear();
        _aimIssuer = 0;
    }

    // Full-saturation hue (0..359) -> packed RGBA (R | G<<8 | B<<16 | A<<24), opaque.
    private static uint HueToRgba(int hue)
    {
        var h = hue / 60;
        var f = (hue % 60) / 60f;
        var q = (byte) (255 * (1 - f));
        var t = (byte) (255 * f);

        var (r, g, b) = h switch
        {
            0 => ((byte) 255, t, (byte) 0),
            1 => (q, (byte) 255, (byte) 0),
            2 => ((byte) 0, (byte) 255, t),
            3 => ((byte) 0, q, (byte) 255),
            4 => (t, (byte) 0, (byte) 255),
            _ => ((byte) 255, (byte) 0, q),
        };

        return (uint) (r | (g << 8) | (b << 16) | (0xFF << 24));
    }
}
