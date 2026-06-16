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

internal sealed class ProbeCommands : ISpCommandCategory
{
    private readonly ExampleContext _ctx;

    public ProbeCommands(ExampleContext ctx) => _ctx = ctx;

    public void Register(IAdminCommandRegistry registry)
    {
        // Read-only serializer probe (discover which fields exist and their encoder type).
        registry.RegisterAdminCommand("sp_probe_scan",  OnProbeScan,  [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_probe_dump",  OnProbeDump,  [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_probe_field", OnProbeField, [ExampleContext.Permission]);
    }

    private void OnProbeScan(IGameClient? issuer, StringCommand command)
    {
        _ctx.Reply(issuer, "sp_probe_scan: scanning live entities (see server log)");
        SerializerProbe.Scan(_ctx.EntityManager, _ctx.Logger);
    }

    private void OnProbeDump(IGameClient? issuer, StringCommand command)
    {
        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var idx))
        {
            _ctx.Reply(issuer, "usage: sp_probe_dump <entityIndex>");

            return;
        }

        _ctx.Reply(issuer, $"sp_probe_dump: dumping entity {idx} (see server log)");
        SerializerProbe.Dump(_ctx.EntityManager, _ctx.Logger, idx);
    }

    private void OnProbeField(IGameClient? issuer, StringCommand command)
    {
        if (command.ArgCount < 2)
        {
            _ctx.Reply(issuer, "usage: sp_probe_field <serializerClass> <fieldName>");

            return;
        }

        _ctx.Reply(issuer, $"sp_probe_field: dumping {command.GetArg(1)}::{command.GetArg(2)} (see server log)");
        SerializerProbe.DumpField(_ctx.EntityManager, _ctx.Logger, command.GetArg(1), command.GetArg(2));
    }
}
