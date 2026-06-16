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

using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace YappersHQ.SendProxy.Example.Commands;

internal sealed class PresetCommands : ISpCommandCategory
{
    private readonly ExampleContext _ctx;

    public PresetCommands(ExampleContext ctx) => _ctx = ctx;

    public void Register(IAdminCommandRegistry registry)
    {
        // Presets — common one-liners built on the same API.
        registry.RegisterAdminCommand("sp_fakehp",   OnFakeHp,   [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_fakename", OnFakeName, [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_sendfake", OnSendFake, [ExampleContext.Permission]);
    }

    // sp_fakehp <n> — shortcut: uniform fake HP on all players.
    private void OnFakeHp(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var value))
        {
            _ctx.Reply(issuer, "usage: sp_fakehp <value>   (0/off via sp_unset CCSPlayerPawn m_iHealth)");

            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_iHealth", value);
        _ctx.Reply(issuer, $"all clients now see {value} HP on every player (real HP unchanged)");
    }

    // sp_fakename <text> — shortcut: uniform fake player name (b5 string).
    private void OnFakeName(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 1)
        {
            _ctx.Reply(issuer, "usage: sp_fakename <text>");

            return;
        }

        var name = ExampleContext.RestOfArgs(command, 1);
        sp.SetUniform("CCSPlayerController", "m_iszPlayerName", name);
        _ctx.Reply(issuer, $"all clients now see \"{name}\" as every player's name");
    }

    // sp_sendfake <n> — one-shot: push fake HUD HP to YOU only, once. Unlike sp_setpc/sp_fakehp this does
    // not persist — it force-dirties m_iPawnHealth so it re-transmits immediately and fakes that single
    // send. Use it to push a value before deciding to Hook. Demonstrates the per-client SendFake path.
    private void OnSendFake(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        if (issuer is null)
        {
            return;
        }

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var value))
        {
            _ctx.Reply(issuer, "usage: sp_sendfake <value>   (one-shot fake HUD HP to you only)");

            return;
        }

        if (issuer.GetPlayerController() is not { } controller)
        {
            _ctx.Reply(issuer, "sp_sendfake: could not resolve your controller entity");

            return;
        }

        // HP lives in two places — controller mirror (m_iPawnHealth) + pawn actual (m_iHealth) — so push
        // both for a consistent HUD. SendFake force-dirties the field, so it shows on the next snapshot
        // (no slap needed) and fires once.
        sp.SendFake(issuer, controller, "CCSPlayerController", "m_iPawnHealth", value);
        if (controller.GetPlayerPawn() is { } pawn)
        {
            sp.SendFake(issuer, pawn, "CCSPlayerPawn", "m_iHealth", value);
        }

        _ctx.Reply(issuer, $"one-shot: you should see {value} HP on the next update (real HP unchanged, fires once, no slap needed)");
    }
}
