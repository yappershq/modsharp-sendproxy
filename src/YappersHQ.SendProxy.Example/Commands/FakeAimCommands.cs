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
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.TargetingManager.Shared;
using Sharp.Shared.Enums;
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
            sp.Hook(ctrl, "CCSPlayerController", "m_iPawnHealth", (nint c, int _, ref int v) => { if (c != issuerPtr) return false; v = 1; return true; });
            sp.Hook(ctrl, "CCSPlayerController", "m_iTeamNum",    (nint c, int _, ref int v) => { if (c != issuerPtr) return false; v = 3; return true; });
            ctrl.NetworkStateChanged("m_iPawnHealth");   // force an initial re-send so it shows at once
            ctrl.NetworkStateChanged("m_iTeamNum");

            if (ctrl.GetPlayerPawn() is { } pawn)
            {
                _aimPawns.Add((int) pawn.Index);
                sp.Hook(pawn, "CCSPlayerPawn", "m_iHealth",          (nint c, int _, ref int v) => { if (c != issuerPtr) return false; v = 1; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_iTeamNum",         (nint c, int _, ref int v) => { if (c != issuerPtr) return false; v = 3; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_clrRender",        (nint c, int _, ref int v) => { if (c != issuerPtr) return false; v = unchecked((int) _aimColor); return true; });
                // Glow: enable a coloured outline (m_iGlowType nonzero) tinted by the same cycling colour.
                // Both are nested under m_Glow; best-effort (enable mechanics / color-encoder may vary).
                sp.Hook(pawn, "CCSPlayerPawn", "m_iGlowType",        (nint c, int _, ref int v) => { if (c != issuerPtr) return false; v = 3; return true; });
                sp.Hook(pawn, "CCSPlayerPawn", "m_glowColorOverride", (nint c, int _, ref int v) => { if (c != issuerPtr) return false; v = unchecked((int) _aimColor); return true; });
                pawn.NetworkStateChanged("m_iHealth");
                pawn.NetworkStateChanged("m_iTeamNum");
                pawn.NetworkStateChanged("m_clrRender");
                pawn.NetworkStateChanged("m_iGlowType");
                pawn.NetworkStateChanged("m_glowColorOverride");
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

    // Timer tick: advance the cycling colour and re-dirty m_clrRender on every target so it re-transmits.
    private void CycleFakeAim()
    {
        _aimHue   = (_aimHue + 15) % 360;
        _aimColor = HueToRgba(_aimHue);

        foreach (var pidx in _aimPawns)
        {
            if (_ctx.EntityManager.FindEntityByIndex((EntityIndex) pidx) is { } pawn)
            {
                pawn.NetworkStateChanged("m_clrRender");
                pawn.NetworkStateChanged("m_glowColorOverride");
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
            ctrl.NetworkStateChanged("m_iPawnHealth");
            ctrl.NetworkStateChanged("m_iTeamNum");
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
            sp.Unhook(pawn, "CCSPlayerPawn", "m_glowColorOverride");
            pawn.NetworkStateChanged("m_iHealth");
            pawn.NetworkStateChanged("m_iTeamNum");
            pawn.NetworkStateChanged("m_clrRender");
            pawn.NetworkStateChanged("m_iGlowType");
            pawn.NetworkStateChanged("m_glowColorOverride");
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
