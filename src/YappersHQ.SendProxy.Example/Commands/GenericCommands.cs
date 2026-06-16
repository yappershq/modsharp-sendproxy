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
using Sharp.Shared.Units;

namespace YappersHQ.SendProxy.Example.Commands;

internal sealed class GenericCommands : ISpCommandCategory
{
    private readonly ExampleContext _ctx;

    public GenericCommands(ExampleContext ctx) => _ctx = ctx;

    public void Register(IAdminCommandRegistry registry)
    {
        // Generic test matrix: any field, any encoder type, any mode.
        registry.RegisterAdminCommand("sp_set",    OnSet,    [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_setpc",  OnSetPc,  [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_setent", OnSetEnt, [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_unset",  OnUnset,  [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_clear",  OnClear,  [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_help",   OnHelp,   [ExampleContext.Permission]);
    }

    // sp_set <serializer> <field> <type> <value...> — uniform spoof (every client) of any field.
    private void OnSet(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 3)
        {
            _ctx.Reply(issuer, "usage: sp_set <serializer> <field> <int|uint|float|bool|vec|string|bytes> <value...>");

            return;
        }

        var ser   = command.GetArg(1);
        var field = command.GetArg(2);
        var type  = command.GetArg(3).ToLowerInvariant();

        switch (type)
        {
            case "int" when ExampleContext.TryInt(command, 4, out var i):    sp.SetUniform(ser, field, i); break;
            case "uint" when ExampleContext.TryInt(command, 4, out var u):   sp.SetUniform(ser, field, u); break;
            case "float" when ExampleContext.TryFloat(command, 4, out var f): sp.SetUniform(ser, field, f); break;
            case "bool" when ExampleContext.TryBool(command, 4, out var b):  sp.SetUniform(ser, field, b); break;
            case "vec" when ExampleContext.TryVec(command, 4, out var v):    sp.SetUniform(ser, field, v); break;
            case "string":                                    sp.SetUniform(ser, field, ExampleContext.RestOfArgs(command, 4)); break;
            case "bytes" when ExampleContext.TryBytes(command, 4, out var by): sp.SetUniform(ser, field, by); break;
            default:
                _ctx.Reply(issuer, $"sp_set: bad type/value for '{type}'. Types: int uint float bool vec string bytes");

                return;
        }

        _ctx.Reply(issuer, $"uniform: {ser}::{field} ({type}) spoofed for all clients");
    }

    // sp_setpc <serializer> <field> <type> — per-client demo: each client sees a value derived from its
    // recipient pointer, proving the per-client path.
    private void OnSetPc(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 3)
        {
            _ctx.Reply(issuer, "usage: sp_setpc <serializer> <field> <int|uint|float|bool|vec|string|bytes>");

            return;
        }

        var ser   = command.GetArg(1);
        var field = command.GetArg(2);
        var type  = command.GetArg(3).ToLowerInvariant();

        switch (type)
        {
            case "int":
            case "uint":
                // 1..64 — small + obvious + varies per client (low value keeps the varint the same byte
                // length as a typical small field so the substitute fits the slot).
                sp.Hook(ser, field, (nint c, int _, ref int v) => { v = 1 + (int) (c & 0x3F); return true; });
                break;
            case "float":
                sp.Hook(ser, field, (nint c, int _, ref float v) => { v = c & 0xFF; return true; });
                break;
            case "bool":
                sp.Hook(ser, field, (nint c, int _, ref bool v) => { v = (c & 1) == 0; return true; });
                break;
            case "vec":
                sp.Hook(ser, field, (nint c, int _, ref System.Numerics.Vector3 v) => { v = new System.Numerics.Vector3(c & 0xFF, 0, 0); return true; });
                break;
            case "string":
                sp.Hook(ser, field, (nint c, int _, ref string v) => { v = $"client-{c & 0xFF}"; return true; });
                break;
            case "bytes":
                sp.Hook(ser, field, (nint c, int _, ref byte[] v) => { v = new[] { (byte) (c & 0xFF) }; return true; });
                break;
            default:
                _ctx.Reply(issuer, $"sp_setpc: unknown type '{type}'. Types: int uint float bool vec string bytes");

                return;
        }

        _ctx.Reply(issuer, $"per-client: {ser}::{field} ({type}) — each client sees a value derived from its recipient ptr");
    }

    // sp_setent <entityIndex> <serializer> <field> <type> <value...> — uniform spoof scoped to one entity.
    private void OnSetEnt(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 4 || !int.TryParse(command.GetArg(1), out var idx))
        {
            _ctx.Reply(issuer, "usage: sp_setent <entityIndex> <serializer> <field> <type> <value...>");

            return;
        }

        if (_ctx.EntityManager.FindEntityByIndex((EntityIndex) idx) is not { } entity)
        {
            _ctx.Reply(issuer, $"no entity at index {idx}");

            return;
        }

        var ser   = command.GetArg(2);
        var field = command.GetArg(3);
        var type  = command.GetArg(4).ToLowerInvariant();

        switch (type)
        {
            case "int" when ExampleContext.TryInt(command, 5, out var i):     sp.SetUniform(entity, ser, field, i); break;
            case "uint" when ExampleContext.TryInt(command, 5, out var u):    sp.SetUniform(entity, ser, field, u); break;
            case "float" when ExampleContext.TryFloat(command, 5, out var f): sp.SetUniform(entity, ser, field, f); break;
            case "bool" when ExampleContext.TryBool(command, 5, out var b):   sp.SetUniform(entity, ser, field, b); break;
            case "vec" when ExampleContext.TryVec(command, 5, out var v):     sp.SetUniform(entity, ser, field, v); break;
            case "string":                                     sp.SetUniform(entity, ser, field, ExampleContext.RestOfArgs(command, 5)); break;
            case "bytes" when ExampleContext.TryBytes(command, 5, out var by): sp.SetUniform(entity, ser, field, by); break;
            default:
                _ctx.Reply(issuer, $"sp_setent: bad type/value for '{type}'");

                return;
        }

        _ctx.Reply(issuer, $"entity #{idx}: {ser}::{field} ({type}) spoofed (other entities unaffected)");
    }

    // sp_unset <serializer> <field> — remove the all-entities registration for a field.
    private void OnUnset(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.SendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 2)
        {
            _ctx.Reply(issuer, "usage: sp_unset <serializer> <field>");

            return;
        }

        sp.Unhook(command.GetArg(1), command.GetArg(2));
        _ctx.Reply(issuer, $"unhooked {command.GetArg(1)}::{command.GetArg(2)}");
    }

    // sp_clear — remove every registration and uninstall the substitution detours.
    private void OnClear(IGameClient? issuer, StringCommand command)
    {
        _ctx.SendProxy?.UnhookAll();
        _ctx.Reply(issuer, "all registrations cleared, substitution detours uninstalled");
    }

    private void OnHelp(IGameClient? issuer, StringCommand command)
    {
        _ctx.Reply(issuer, "SendProxy test matrix:");
        _ctx.Reply(issuer, "  sp_set    <ser> <field> <type> <value...>   uniform (all clients)");
        _ctx.Reply(issuer, "  sp_setpc  <ser> <field> <type>              per-client (value varies per recipient)");
        _ctx.Reply(issuer, "  sp_setent <idx> <ser> <field> <type> <val>  scoped to one entity");
        _ctx.Reply(issuer, "  sp_sendfake <value>                         one-shot fake HUD HP to you only (force-resend)");
        _ctx.Reply(issuer, "  sp_unset  <ser> <field>   |   sp_clear   |   sp_help");
        _ctx.Reply(issuer, "types -> encoder bucket: int=b1  uint=b2  vec=b3(qangle/vector/coord/quantized)  float=b4  string=b5  bytes=b6  bool=b7");
        _ctx.Reply(issuer, "examples:");
        _ctx.Reply(issuer, "  sp_set CCSPlayerPawn m_iHealth int 1337");
        _ctx.Reply(issuer, "  sp_set CCSPlayerController m_iszPlayerName string Hacker");
        _ctx.Reply(issuer, "  sp_setpc CCSPlayerPawn m_iHealth int        (each client a different HP)");
        _ctx.Reply(issuer, "  sp_setent 3 CCSPlayerPawn m_iHealth int 1   (only entity #3)");
        _ctx.Reply(issuer, "canned per-bucket demos: sp_encoder1..7 (1=int 2=uint 3=qangle 4=float 5=string 6=bytes 7=bool), sp_encoders_off to revert");
        _ctx.Reply(issuer, "  sp_fakeaim <target>   per-client+per-entity: fake HP/team/colour/glow on a target (@aim/nick/@t), only YOU see it; sp_fakeaim_off");
        _ctx.Reply(issuer, "  sp_fakehp <n> | sp_fakename <text>            quick uniform presets");
        _ctx.Reply(issuer, "discover fields/types with sp_probe_dump <entityIndex> / sp_probe_field <ser> <field>");
    }
}
