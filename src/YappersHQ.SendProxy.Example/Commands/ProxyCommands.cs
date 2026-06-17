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

using System.Globalization;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Example.Commands;

// Exercises the NEW proxy-field API (IProxyManager): one callback fired once per (entity, field) in the
// shared pack. sp_proxy registers a uniform proxy (SetAll → every client); sp_proxy_off removes it.
internal sealed class ProxyCommands : ISpCommandCategory
{
    private readonly ExampleContext _ctx;

    public ProxyCommands(ExampleContext ctx) => _ctx = ctx;

    public void Register(IAdminCommandRegistry registry)
    {
        registry.RegisterAdminCommand("sp_proxy",        OnProxy,       [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_proxy_off",    OnProxyOff,    [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_proxyfor",     OnProxyFor,    [ExampleContext.Permission]);
        registry.RegisterAdminCommand("sp_proxyfor_off", OnProxyForOff, [ExampleContext.Permission]);
    }

    // sp_proxyfor <serializer> <field> <int> <value> — PER-VIEWER proxy: only YOU (the issuer) see the
    // faked value; every other client sees the real value. Exercises ctx.SetFor (the per-recipient path).
    private void OnProxyFor(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.Proxy is not { } proxy)
        {
            _ctx.Reply(issuer, "sp_proxyfor: IProxyManager not available");

            return;
        }

        if (issuer is null)
        {
            _ctx.Reply(issuer, "sp_proxyfor: run from a connected client (needs a viewer)");

            return;
        }

        if (command.ArgCount < 4 || !int.TryParse(command.GetArg(4), out var iv))
        {
            _ctx.Reply(issuer, "usage: sp_proxyfor <serializer> <field> int <value>");

            return;
        }

        var ser    = command.GetArg(1);
        var field  = command.GetArg(2);
        var viewer = issuer; // capture: only this client sees the fake

        proxy.Register(ser, field, (ref ProxyContext ctx) => ctx.SetFor(viewer, SpoofValue.Int(iv)));
        _ctx.Reply(issuer, $"sp_proxyfor: {ser}::{field} = {iv} — only YOU see it (per-viewer, live)");
    }

    private void OnProxyForOff(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.Proxy is not { } proxy || command.ArgCount < 2)
        {
            _ctx.Reply(issuer, "usage: sp_proxyfor_off <serializer> <field>");

            return;
        }

        proxy.Unregister(command.GetArg(1), command.GetArg(2));
        _ctx.Reply(issuer, $"sp_proxyfor_off: removed {command.GetArg(1)}::{command.GetArg(2)}");
    }

    // sp_proxy <serializer> <field> <int|float|bool> <value> — uniform proxy via the new IProxyManager API.
    private void OnProxy(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.Proxy is not { } proxy)
        {
            _ctx.Reply(issuer, "sp_proxy: IProxyManager not available");

            return;
        }

        if (command.ArgCount < 4)
        {
            _ctx.Reply(issuer, "usage: sp_proxy <serializer> <field> <int|float|bool> <value>");

            return;
        }

        var ser   = command.GetArg(1);
        var field = command.GetArg(2);
        var type  = command.GetArg(3).ToLowerInvariant();
        var raw   = command.GetArg(4);

        switch (type)
        {
            case "int":
            case "uint":
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                {
                    _ctx.Reply(issuer, "sp_proxy: bad int value");

                    return;
                }

                proxy.Register(ser, field, (ref ProxyContext ctx) => ctx.SetAll(SpoofValue.Int(iv)));
                break;

            case "float":
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                {
                    _ctx.Reply(issuer, "sp_proxy: bad float value");

                    return;
                }

                proxy.Register(ser, field, (ref ProxyContext ctx) => ctx.SetAll(SpoofValue.Float(fv)));
                break;

            case "bool":
                var bv = raw is "1" or "true" or "yes";
                proxy.Register(ser, field, (ref ProxyContext ctx) => ctx.SetAll(SpoofValue.Bool(bv)));
                break;

            default:
                _ctx.Reply(issuer, $"sp_proxy: unknown type '{type}' (int|float|bool)");

                return;
        }

        _ctx.Reply(issuer, $"sp_proxy: uniform proxy set on {ser}::{field} = {raw} (every client, live)");
    }

    private void OnProxyOff(IGameClient? issuer, StringCommand command)
    {
        if (_ctx.Proxy is not { } proxy)
        {
            return;
        }

        if (command.ArgCount < 2)
        {
            _ctx.Reply(issuer, "usage: sp_proxy_off <serializer> <field>");

            return;
        }

        proxy.Unregister(command.GetArg(1), command.GetArg(2));
        _ctx.Reply(issuer, $"sp_proxy_off: removed proxy on {command.GetArg(1)}::{command.GetArg(2)}");
    }
}
