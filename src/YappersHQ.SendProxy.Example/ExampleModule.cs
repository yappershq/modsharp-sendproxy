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
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Example;

/// <summary>
///     Example consumer of <see cref="ISendProxyManager"/> — a field-substitution test bench. The generic
///     commands (<c>sp_set</c> / <c>sp_setpc</c> / <c>sp_setent</c>) exercise every encoder type across
///     every mode against any field, so a tester can hit the whole matrix without recompiling; a handful
///     of presets and the read-only serializer probe round it out. All commands are AdminManager-gated;
///     the Core library ships none.
/// </summary>
public sealed class ExampleModule : IModSharpModule
{
    private const string AdminManagerAssemblyName = "Sharp.Modules.AdminManager";
    private const string SendProxyPermission      = "sendproxy:example";

    private static readonly string ModuleIdentity =
        typeof(ExampleModule).Assembly.GetName().Name ?? "YappersHQ.SendProxy.Example";

    private readonly ILogger<ExampleModule> _logger;
    private readonly IEntityManager         _entityManager;
    private readonly IClientManager         _clientManager;
    private readonly ISharpModuleManager    _modules;

    private ISendProxyManager?                       _sendProxy;
    private IModSharpModuleInterface<IAdminManager>? _adminManager;
    private bool                                     _commandsRegistered;

    public ExampleModule(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        _logger        = sharedSystem.GetLoggerFactory().CreateLogger<ExampleModule>();
        _entityManager = sharedSystem.GetEntityManager();
        _clientManager = sharedSystem.GetClientManager();
        _modules       = sharedSystem.GetSharpModuleManager();
    }

    public string DisplayName   => "SendProxy Example";
    public string DisplayAuthor => "YappersHQ";

    #region IModSharpModule

    public bool Init()
        => true;

    public void OnLibraryConnected(string name)
    {
        if (name.Equals(AdminManagerAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            TryRegisterCommands();
        }
    }

    // Publishers register their interface in PostInit; consumers resolve in OnAllModulesLoaded
    // (ModSharp guarantees all PostInits run before any OnAllModulesLoaded).
    public void OnAllModulesLoaded()
    {
        _sendProxy = _modules
            .GetOptionalSharpModuleInterface<ISendProxyManager>(ISendProxyManager.Identity)?.Instance;

        if (_sendProxy is null)
        {
            _logger.LogWarning("SendProxy not loaded — example disabled");

            return;
        }

        TryRegisterCommands();
    }

    public void Shutdown()
        => _sendProxy?.UnhookAll();

    #endregion

    #region Command registration

    private void TryRegisterCommands()
    {
        if (_commandsRegistered || _sendProxy is null)
        {
            return;
        }

        _adminManager ??= _modules.GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);
        if (_adminManager?.Instance is not { } adminManager)
        {
            return;
        }

        try
        {
            var registry = adminManager.GetCommandRegistry(ModuleIdentity);

            // RegisterAdminCommand does not auto-register the permission; do it so the flag resolves under
            // wildcards. Operators grant "sendproxy:example" (or "*") via their real admin source.
            registry.RegisterPermissions([SendProxyPermission]);

            // Generic test matrix: any field, any encoder type, any mode.
            registry.RegisterAdminCommand("sp_set",    OnSet,    [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_setpc",  OnSetPc,  [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_setent", OnSetEnt, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_unset",  OnUnset,  [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_clear",  OnClear,  [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_help",   OnHelp,   [SendProxyPermission]);

            // Presets — common one-liners built on the same API.
            registry.RegisterAdminCommand("sp_fakehp",   OnFakeHp,   [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_fakename", OnFakeName, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_sendfake", OnSendFake, [SendProxyPermission]);

            // One canned real-use demo per encoder bucket (sp_encoder1..7) + a single off switch.
            registry.RegisterAdminCommand("sp_encoder1", OnEncoder1, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_encoder2", OnEncoder2, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_encoder3", OnEncoder3, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_encoder4", OnEncoder4, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_encoder5", OnEncoder5, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_encoder6", OnEncoder6, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_encoder7", OnEncoder7, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_encoders_off", OnEncodersOff, [SendProxyPermission]);

            // Read-only serializer probe (discover which fields exist and their encoder type).
            registry.RegisterAdminCommand("sp_probe_scan",  OnProbeScan,  [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_probe_dump",  OnProbeDump,  [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_probe_field", OnProbeField, [SendProxyPermission]);

            _commandsRegistered = true;
            _logger.LogInformation("SendProxy example admin commands registered under \"{Perm}\"", SendProxyPermission);
        }
        catch (InvalidOperationException)
        {
            // CommandCenter isn't loaded yet — retry on OnLibraryConnected.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to register SendProxy example admin commands.");
        }
    }

    #endregion

    #region Generic test-matrix commands

    // sp_set <serializer> <field> <type> <value...> — uniform spoof (every client) of any field.
    private void OnSet(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 3)
        {
            Reply(issuer, "usage: sp_set <serializer> <field> <int|uint|float|bool|vec|string|bytes> <value...>");

            return;
        }

        var ser   = command.GetArg(1);
        var field = command.GetArg(2);
        var type  = command.GetArg(3).ToLowerInvariant();

        switch (type)
        {
            case "int" when TryInt(command, 4, out var i):    sp.SetUniform(ser, field, i); break;
            case "uint" when TryInt(command, 4, out var u):   sp.SetUniform(ser, field, u); break;
            case "float" when TryFloat(command, 4, out var f): sp.SetUniform(ser, field, f); break;
            case "bool" when TryBool(command, 4, out var b):  sp.SetUniform(ser, field, b); break;
            case "vec" when TryVec(command, 4, out var v):    sp.SetUniform(ser, field, v); break;
            case "string":                                    sp.SetUniform(ser, field, RestOfArgs(command, 4)); break;
            case "bytes" when TryBytes(command, 4, out var by): sp.SetUniform(ser, field, by); break;
            default:
                Reply(issuer, $"sp_set: bad type/value for '{type}'. Types: int uint float bool vec string bytes");

                return;
        }

        Reply(issuer, $"uniform: {ser}::{field} ({type}) spoofed for all clients");
    }

    // sp_setpc <serializer> <field> <type> — per-client demo: each client sees a value derived from its
    // recipient pointer, proving the per-client path.
    private void OnSetPc(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 3)
        {
            Reply(issuer, "usage: sp_setpc <serializer> <field> <int|uint|float|bool|vec|string|bytes>");

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
                sp.Hook(ser, field, (nint c, int _, ref Vector3 v) => { v = new Vector3(c & 0xFF, 0, 0); return true; });
                break;
            case "string":
                sp.Hook(ser, field, (nint c, int _, ref string v) => { v = $"client-{c & 0xFF}"; return true; });
                break;
            case "bytes":
                sp.Hook(ser, field, (nint c, int _, ref byte[] v) => { v = new[] { (byte) (c & 0xFF) }; return true; });
                break;
            default:
                Reply(issuer, $"sp_setpc: unknown type '{type}'. Types: int uint float bool vec string bytes");

                return;
        }

        Reply(issuer, $"per-client: {ser}::{field} ({type}) — each client sees a value derived from its recipient ptr");
    }

    // sp_setent <entityIndex> <serializer> <field> <type> <value...> — uniform spoof scoped to one entity.
    private void OnSetEnt(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 4 || !int.TryParse(command.GetArg(1), out var idx))
        {
            Reply(issuer, "usage: sp_setent <entityIndex> <serializer> <field> <type> <value...>");

            return;
        }

        if (_entityManager.FindEntityByIndex((EntityIndex) idx) is not { } entity)
        {
            Reply(issuer, $"no entity at index {idx}");

            return;
        }

        var ser   = command.GetArg(2);
        var field = command.GetArg(3);
        var type  = command.GetArg(4).ToLowerInvariant();

        switch (type)
        {
            case "int" when TryInt(command, 5, out var i):     sp.SetUniform(entity, ser, field, i); break;
            case "uint" when TryInt(command, 5, out var u):    sp.SetUniform(entity, ser, field, u); break;
            case "float" when TryFloat(command, 5, out var f): sp.SetUniform(entity, ser, field, f); break;
            case "bool" when TryBool(command, 5, out var b):   sp.SetUniform(entity, ser, field, b); break;
            case "vec" when TryVec(command, 5, out var v):     sp.SetUniform(entity, ser, field, v); break;
            case "string":                                     sp.SetUniform(entity, ser, field, RestOfArgs(command, 5)); break;
            case "bytes" when TryBytes(command, 5, out var by): sp.SetUniform(entity, ser, field, by); break;
            default:
                Reply(issuer, $"sp_setent: bad type/value for '{type}'");

                return;
        }

        Reply(issuer, $"entity #{idx}: {ser}::{field} ({type}) spoofed (other entities unaffected)");
    }

    // sp_unset <serializer> <field> — remove the all-entities registration for a field.
    private void OnUnset(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 2)
        {
            Reply(issuer, "usage: sp_unset <serializer> <field>");

            return;
        }

        sp.Unhook(command.GetArg(1), command.GetArg(2));
        Reply(issuer, $"unhooked {command.GetArg(1)}::{command.GetArg(2)}");
    }

    // sp_clear — remove every registration and uninstall the substitution detours.
    private void OnClear(IGameClient? issuer, StringCommand command)
    {
        _sendProxy?.UnhookAll();
        Reply(issuer, "all registrations cleared, substitution detours uninstalled");
    }

    private void OnHelp(IGameClient? issuer, StringCommand command)
    {
        Reply(issuer, "SendProxy test matrix:");
        Reply(issuer, "  sp_set    <ser> <field> <type> <value...>   uniform (all clients)");
        Reply(issuer, "  sp_setpc  <ser> <field> <type>              per-client (value varies per recipient)");
        Reply(issuer, "  sp_setent <idx> <ser> <field> <type> <val>  scoped to one entity");
        Reply(issuer, "  sp_sendfake <value>                         one-shot fake HUD HP to you only (force-resend)");
        Reply(issuer, "  sp_unset  <ser> <field>   |   sp_clear   |   sp_help");
        Reply(issuer, "types -> encoder bucket: int=b1  uint=b2  vec=b3(qangle/vector/coord/quantized)  float=b4  string=b5  bytes=b6  bool=b7");
        Reply(issuer, "examples:");
        Reply(issuer, "  sp_set CCSPlayerPawn m_iHealth int 1337");
        Reply(issuer, "  sp_set CCSPlayerController m_iszPlayerName string Hacker");
        Reply(issuer, "  sp_setpc CCSPlayerPawn m_iHealth int        (each client a different HP)");
        Reply(issuer, "  sp_setent 3 CCSPlayerPawn m_iHealth int 1   (only entity #3)");
        Reply(issuer, "canned per-bucket demos: sp_encoder1..7 (1=int 2=uint 3=qangle 4=float 5=string 6=bytes 7=bool), sp_encoders_off to revert");
        Reply(issuer, "discover fields/types with sp_probe_dump <entityIndex> / sp_probe_field <ser> <field>");
    }

    #endregion

    #region Presets

    // sp_fakehp <n> — shortcut: uniform fake HP on all players.
    private void OnFakeHp(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var value))
        {
            Reply(issuer, "usage: sp_fakehp <value>   (0/off via sp_unset CCSPlayerPawn m_iHealth)");

            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_iHealth", value);
        Reply(issuer, $"all clients now see {value} HP on every player (real HP unchanged)");
    }

    // sp_fakename <text> — shortcut: uniform fake player name (b5 string).
    private void OnFakeName(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        if (command.ArgCount < 1)
        {
            Reply(issuer, "usage: sp_fakename <text>");

            return;
        }

        var name = RestOfArgs(command, 1);
        sp.SetUniform("CCSPlayerController", "m_iszPlayerName", name);
        Reply(issuer, $"all clients now see \"{name}\" as every player's name");
    }

    // sp_sendfake <n> — one-shot: push fake HUD HP to YOU only, once. Unlike sp_setpc/sp_fakehp this does
    // not persist — it force-dirties m_iPawnHealth so it re-transmits immediately and fakes that single
    // send. Use it to push a value before deciding to Hook. Demonstrates the per-client SendFake path.
    private void OnSendFake(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        if (issuer is null)
        {
            return;
        }

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var value))
        {
            Reply(issuer, "usage: sp_sendfake <value>   (one-shot fake HUD HP to you only)");

            return;
        }

        if (issuer.GetPlayerController() is not { } controller)
        {
            Reply(issuer, "sp_sendfake: could not resolve your controller entity");

            return;
        }

        sp.SendFake(issuer, controller, "CCSPlayerController", "m_iPawnHealth", value);
        Reply(issuer, $"one-shot: you should see {value} HP on the next update (real HP unchanged, fires once)");
    }

    #endregion

    #region Per-encoder real-use demos (sp_encoder1..7)

    // Force a field to re-transmit right now (mark it dirty on every player) so a uniform spoof shows
    // immediately — no need to wait for the value to change naturally (a slap, a move, etc.). This is the
    // "do it from the plugin alone" trigger: NetworkStateChanged sets the engine's per-field dirty bit, so
    // the field lands in the next snapshot and the uniform encoder hook fakes whatever is re-sent. Routes
    // to the controller for CCSPlayerController fields and to the pawn otherwise.
    private void ForceResendAll(string serializerName, string fieldName)
    {
        var onController = serializerName.Contains("Controller", StringComparison.Ordinal);
        foreach (var client in _clientManager.GetGameClients(inGame: true))
        {
            // Bots have no screen and the engine never sends them snapshots, so there's nothing to
            // re-transmit on their behalf — skip them.
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            if (client.GetPlayerController() is not { } controller)
            {
                continue;
            }

            if (onController)
            {
                controller.NetworkStateChanged(fieldName);
            }
            else if (controller.GetPlayerPawn() is { } pawn)
            {
                pawn.NetworkStateChanged(fieldName);
            }
        }
    }

    // One canned demo per encoder bucket, each on a real networked field. sp_encoders_off reverts.
    // bucket 1 — signed int — fake HP. The HUD reads CCSPlayerController::m_iPawnHealth (not the pawn's
    // m_iHealth), so spoof both: m_iPawnHealth drives the on-screen number, m_iHealth the pawn value.
    private void OnEncoder1(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerController", "m_iPawnHealth", 1337);
        sp.SetUniform("CCSPlayerPawn", "m_iHealth", 1337);
        ForceResendAll("CCSPlayerController", "m_iPawnHealth");
        ForceResendAll("CCSPlayerPawn", "m_iHealth");
        Reply(issuer, "enc1 (int b1): m_iPawnHealth (HUD) + m_iHealth = 1337 — shown immediately (no slap needed)");
    }

    // bucket 2 — unsigned int — m_iTeamNum (uint8): every player shows as CT to all clients. m_iTeamNum
    // lives on CBaseEntity, so BOTH the pawn (radar/outline/world model) and the controller (scoreboard)
    // carry it — spoof both for the full effect. Real use: disguise team membership.
    private void OnEncoder2(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_iTeamNum", 3);
        sp.SetUniform("CCSPlayerController", "m_iTeamNum", 3);
        ForceResendAll("CCSPlayerPawn", "m_iTeamNum");
        ForceResendAll("CCSPlayerController", "m_iTeamNum");
        Reply(issuer, "enc2 (uint b2): m_iTeamNum = 3 (CT) on pawn + controller — all players appear CT (radar/outline + scoreboard)");
    }

    // bucket 3-family float — m_flScale (model scale): every player renders tiny (0.3x) to all clients.
    // Dramatic + actually rendered from the netvar (server keeps real scale=1, so hitboxes/collision are
    // unchanged — purely visual). Observe another player or a bot to see it. m_flScale is a networked leaf
    // (unlike m_vecViewOffset, whose three quantized sub-fields share the bare names m_vecX/Y/Z with
    // m_vecOrigin — uniform matches by name only, so spoofing it would also teleport everyone — and whose
    // Z clamps to 0..64). The qangle/coord/quantized encoder itself is proven by the quantized struct-dump.
    private void OnEncoder3(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_flScale", 0.3f);
        ForceResendAll("CCSPlayerPawn", "m_flScale");
        Reply(issuer, "enc3 (float): m_flScale = 0.3 — every player renders tiny to all clients (real size unchanged). Observe another player/bot. sp_encoders_off to clear.");
    }

    // bucket 4 — float — m_flFlashDuration: every client renders a flash-blind white-out. A genuinely
    // float-encoded field whose effect is unmistakable (unlike m_flViewmodelFOV, a quantized float that
    // substitutes correctly on the wire but isn't visibly rendered — own-view + client viewmodel_fov cvar).
    private void OnEncoder4(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        // Flash render needs both: max alpha (intensity 0..255) AND duration (how long it holds/fades).
        sp.SetUniform("CCSPlayerPawn", "m_flFlashMaxAlpha", 255f);
        sp.SetUniform("CCSPlayerPawn", "m_flFlashDuration", 5f);
        ForceResendAll("CCSPlayerPawn", "m_flFlashMaxAlpha");
        ForceResendAll("CCSPlayerPawn", "m_flFlashDuration");
        Reply(issuer, "enc4 (float b4): m_flFlashMaxAlpha=255 + m_flFlashDuration=5 — every client flashed white (visible float). sp_encoders_off to clear.");
    }

    // bucket 5 — string — m_iszPlayerName: every player shows the same name.
    private void OnEncoder5(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerController", "m_iszPlayerName", "SendProxyTest");
        ForceResendAll("CCSPlayerController", "m_iszPlayerName");
        Reply(issuer, "enc5 (string b5): CCSPlayerController::m_iszPlayerName = \"SendProxyTest\" for all clients");
    }

    // bucket 6 — byte-array — no common byte-array netvar exists on the player schema; point the tester
    // at the generic path so they can exercise b6 on whatever field they find via the probe.
    private void OnEncoder6(IGameClient? issuer, StringCommand command)
    {
        Reply(issuer, "enc6 (bytes b6): no common byte-array field on players. Find one via sp_probe_dump,");
        Reply(issuer, "then: sp_set <serializer> <field> bytes <hexstring>   (e.g. bytes DEADBEEF)");
    }

    // bucket 7 — bool — m_bIsScoped: every player appears scoped.
    private void OnEncoder7(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        sp.SetUniform("CCSPlayerPawn", "m_bIsScoped", true);
        ForceResendAll("CCSPlayerPawn", "m_bIsScoped");
        Reply(issuer, "enc7 (bool b7): CCSPlayerPawn::m_bIsScoped = true — all players appear scoped to all clients");
    }

    private void OnEncodersOff(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is not { } sp)
        {
            return;
        }

        sp.Unhook("CCSPlayerController", "m_iPawnHealth");
        sp.Unhook("CCSPlayerPawn", "m_iHealth");
        sp.Unhook("CCSPlayerPawn", "m_iTeamNum");
        sp.Unhook("CCSPlayerController", "m_iTeamNum");
        sp.Unhook("CCSPlayerPawn", "m_flScale");
        sp.Unhook("CCSPlayerPawn", "m_flFlashMaxAlpha");
        sp.Unhook("CCSPlayerPawn", "m_flFlashDuration");
        sp.Unhook("CCSPlayerController", "m_iszPlayerName");
        sp.Unhook("CCSPlayerPawn", "m_bIsScoped");
        Reply(issuer, "sp_encoders_off: reverted all sp_encoder1..7 demos");
    }

    #endregion

    #region Serializer probe (read-only)

    private void OnProbeScan(IGameClient? issuer, StringCommand command)
    {
        Reply(issuer, "sp_probe_scan: scanning live entities (see server log)");
        SerializerProbe.Scan(_entityManager, _logger);
    }

    private void OnProbeDump(IGameClient? issuer, StringCommand command)
    {
        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var idx))
        {
            Reply(issuer, "usage: sp_probe_dump <entityIndex>");

            return;
        }

        Reply(issuer, $"sp_probe_dump: dumping entity {idx} (see server log)");
        SerializerProbe.Dump(_entityManager, _logger, idx);
    }

    private void OnProbeField(IGameClient? issuer, StringCommand command)
    {
        if (command.ArgCount < 2)
        {
            Reply(issuer, "usage: sp_probe_field <serializerClass> <fieldName>");

            return;
        }

        Reply(issuer, $"sp_probe_field: dumping {command.GetArg(1)}::{command.GetArg(2)} (see server log)");
        SerializerProbe.DumpField(_entityManager, _logger, command.GetArg(1), command.GetArg(2));
    }

    #endregion

    #region Helpers

    private void Reply(IGameClient? issuer, string message)
    {
        if (issuer?.GetPlayerController()?.GetPlayerPawn() is { } pawn)
        {
            pawn.Print(HudPrintChannel.Chat, message);
        }
        else
        {
            _logger.LogInformation("{Message}", message);
        }
    }

    private static bool TryInt(StringCommand cmd, int argIndex, out int value)
    {
        value = 0;
        if (cmd.ArgCount < argIndex
            || !long.TryParse(cmd.GetArg(argIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return false;
        }

        value = unchecked((int) l);

        return true;
    }

    private static bool TryFloat(StringCommand cmd, int argIndex, out float value)
    {
        value = 0f;

        return cmd.ArgCount >= argIndex
            && float.TryParse(cmd.GetArg(argIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryBool(StringCommand cmd, int argIndex, out bool value)
    {
        value = false;
        if (cmd.ArgCount < argIndex)
        {
            return false;
        }

        var s = cmd.GetArg(argIndex);
        if (s is "1" or "true" or "yes")
        {
            value = true;

            return true;
        }

        return s is "0" or "false" or "no";
    }

    private static bool TryVec(StringCommand cmd, int argIndex, out Vector3 value)
    {
        value = default;
        if (cmd.ArgCount < argIndex + 2
            || !float.TryParse(cmd.GetArg(argIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(cmd.GetArg(argIndex + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(cmd.GetArg(argIndex + 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        value = new Vector3(x, y, z);

        return true;
    }

    // Parse a contiguous hex string (e.g. "DEADBEEF") into bytes.
    private static bool TryBytes(StringCommand cmd, int argIndex, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (cmd.ArgCount < argIndex)
        {
            return false;
        }

        var hex = cmd.GetArg(argIndex);
        if (hex.Length == 0 || (hex.Length & 1) != 0)
        {
            return false;
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return false;
            }
        }

        value = bytes;

        return true;
    }

    // Join arg[from..ArgCount] with spaces (for free-text string values).
    private static string RestOfArgs(StringCommand cmd, int from)
    {
        if (cmd.ArgCount < from)
        {
            return string.Empty;
        }

        var parts = new string[cmd.ArgCount - from + 1];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = cmd.GetArg(from + i);
        }

        return string.Join(' ', parts);
    }

    #endregion
}
