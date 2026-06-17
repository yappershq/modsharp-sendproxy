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

using System.Numerics;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Example.Commands;

internal sealed class EncoderDemoCommands : ISpCommandCategory
{
    private readonly ExampleContext _ctx;

    public EncoderDemoCommands(ExampleContext ctx) => _ctx = ctx;

    public void Register(IAdminCommandRegistry registry)
    {
        // One canned real-use demo per encoder bucket (sp_encoder1..7) + a single off switch.
        registry.RegisterAdminCommand("sp_encoder1",     OnEncoder1,     [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_encoder2",     OnEncoder2,     [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_encoder3",     OnEncoder3,     [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_encoder4",     OnEncoder4,     [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_encoder5",     OnEncoder5,     [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_encoder6",     OnEncoder6,     [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_encoder7",     OnEncoder7,     [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_encoders_off", OnEncodersOff,  [ExampleContext.Permission]);
    }

    // One canned demo per encoder bucket, each on a real networked field. sp_encoders_off reverts.
    // bucket 1 — signed int — fake HP. The HUD reads CCSPlayerController::m_iPawnHealth (not the pawn's
    // m_iHealth), so spoof both: m_iPawnHealth drives the on-screen number, m_iHealth the pawn value.
    private void OnEncoder1(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerController", "m_iPawnHealth", SpoofValue.Int(1337));
        sp.SetUniform("CCSPlayerPawn", "m_iHealth", SpoofValue.Int(1337));
        _ctx.ForceResendAll("CCSPlayerController", "m_iPawnHealth");
        _ctx.ForceResendAll("CCSPlayerPawn", "m_iHealth");
        _ctx.Reply(issuer, "enc1 (int b1): m_iPawnHealth (HUD) + m_iHealth = 1337 — shown immediately (no slap needed)");
    }

    // bucket 2 — unsigned int — m_iTeamNum (uint8): every player shows as CT to all clients. m_iTeamNum
    // lives on CBaseEntity, so BOTH the pawn (radar/outline/world model) and the controller (scoreboard)
    // carry it — spoof both for the full effect. Real use: disguise team membership.
    private void OnEncoder2(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_iTeamNum", SpoofValue.Int(3));
        sp.SetUniform("CCSPlayerController", "m_iTeamNum", SpoofValue.Int(3));
        _ctx.ForceResendAll("CCSPlayerPawn", "m_iTeamNum");
        _ctx.ForceResendAll("CCSPlayerController", "m_iTeamNum");
        _ctx.Reply(issuer, "enc2 (uint b2): m_iTeamNum = 3 (CT) on pawn + controller — all players appear CT (radar/outline + scoreboard)");
    }

    // bucket 3 — qangle — m_angEyeAngles (encoder qangle_precise): flips where every player appears to be
    // looking. An "others see you" field — your OWN view is client-controlled, so observe another player or
    // a bot (or spectate) to see it. The b3 family (qangle/coord/quantized) is also proven on the wire by
    // the quantized struct-dump diag. (m_vecViewOffset can't be uniform-spoofed: its three quantized
    // sub-fields share the bare names m_vecX/Y/Z with m_vecOrigin, and uniform matches by name only.)
    private void OnEncoder3(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_angEyeAngles", SpoofValue.Vector(new Vector3(0f, 180f, 0f)));
        _ctx.ForceResendAll("CCSPlayerPawn", "m_angEyeAngles");
        _ctx.Reply(issuer, "enc3 (qangle b3): m_angEyeAngles = (0,180,0) — fakes where a player is looking (their camera). SPECTATE someone to see it: their spectate view points the spoofed way. sp_encoders_off to clear.");
    }

    // bucket 4 — float — m_flScale (model scale): every player renders tiny (0.3x) to all clients.
    // Dramatic + actually rendered from the netvar; server keeps real scale = 1, so hitboxes/collision are
    // unchanged (purely visual). A networked leaf, so the resend resolves. Observe another player or a bot.
    private void OnEncoder4(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_flScale", SpoofValue.Float(0.3f));
        _ctx.ForceResendAll("CCSPlayerPawn", "m_flScale");
        _ctx.Reply(issuer, "enc4 (float b4): m_flScale = 0.3 — every player renders tiny to all clients (real size unchanged). Observe another player/bot. sp_encoders_off to clear.");
    }

    // bucket 5 — string — m_iszPlayerName: every player shows the same name.
    private void OnEncoder5(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerController", "m_iszPlayerName", SpoofValue.String("SendProxyTest"));
        _ctx.ForceResendAll("CCSPlayerController", "m_iszPlayerName");
        _ctx.Reply(issuer, "enc5 (string b5): CCSPlayerController::m_iszPlayerName = \"SendProxyTest\" for all clients");
    }

    // bucket 6 — byte-array — no common byte-array netvar exists on the player schema; point the tester
    // at the generic path so they can exercise b6 on whatever field they find via the probe.
    private void OnEncoder6(IGameClient? issuer, StringCommand command)
    {
        _ctx.Reply(issuer, "enc6 (bytes b6): no common byte-array field on players. Find one via sp_probe_dump,");
        _ctx.Reply(issuer, "then: sp_set <serializer> <field> bytes <hexstring>   (e.g. bytes DEADBEEF)");
    }

    // bucket 7 — bool — m_bIsScoped: every player appears scoped.
    private void OnEncoder7(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_bIsScoped", SpoofValue.Bool(true));
        _ctx.ForceResendAll("CCSPlayerPawn", "m_bIsScoped");
        _ctx.Reply(issuer, "enc7 (bool b7): CCSPlayerPawn::m_bIsScoped = true — all players appear scoped to all clients");
    }

    private void OnEncodersOff(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        (string ser, string field)[] fields =
        {
            ("CCSPlayerController", "m_iPawnHealth"),
            ("CCSPlayerPawn", "m_iHealth"),
            ("CCSPlayerPawn", "m_iTeamNum"),
            ("CCSPlayerController", "m_iTeamNum"),
            ("CCSPlayerPawn", "m_angEyeAngles"),
            ("CCSPlayerPawn", "m_flScale"),
            ("CCSPlayerController", "m_iszPlayerName"),
            ("CCSPlayerPawn", "m_bIsScoped"),
        };

        foreach (var (ser, field) in fields)
        {
            sp.Unhook(ser, field);
        }

        // Unhook only STOPS the spoof — clients still hold the last fake value, and a static field isn't
        // re-sent until it changes. Force a resend AFTER unhooking so the real value goes back out and
        // every client reverts immediately (otherwise they'd stay faked until the field naturally changed).
        foreach (var (ser, field) in fields)
        {
            _ctx.ForceResendAll(ser, field);
        }

        _ctx.Reply(issuer, "sp_encoders_off: reverted all sp_encoder1..7 demos (real values re-sent)");
    }
}
