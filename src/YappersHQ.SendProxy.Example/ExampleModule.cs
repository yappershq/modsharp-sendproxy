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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Example;

/// <summary>
///     Example consumer of <see cref="ISendProxyManager"/>. Demonstrates the full public API — uniform
///     spoofs, per-entity spoofs, per-client callbacks, vector hooks — plus the read-only serializer
///     probe. All commands are registered through AdminManager (admin-gated, dispatched from chat,
///     console and RCON); the Core library ships no commands.
/// </summary>
public sealed class ExampleModule : IModSharpModule
{
    private const string AdminManagerAssemblyName = "Sharp.Modules.AdminManager";
    private const string SendProxyPermission      = "sendproxy:example";

    private static readonly string ModuleIdentity =
        typeof(ExampleModule).Assembly.GetName().Name ?? "YappersHQ.SendProxy.Example";

    private readonly ISharedSystem        _sharedSystem;
    private readonly ILogger<ExampleModule> _logger;
    private readonly IEntityManager       _entityManager;
    private readonly ISharpModuleManager  _modules;

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
        _sharedSystem  = sharedSystem;
        _logger        = sharedSystem.GetLoggerFactory().CreateLogger<ExampleModule>();
        _entityManager = sharedSystem.GetEntityManager();
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
    {
        // AdminManager auto-unregisters this module's commands and permissions on disconnect, so only the
        // per-client registrations need cleaning up here.
        _sendProxy?.UnhookAllPerClient();
    }

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

            // RegisterAdminCommand does not auto-register permissions; do it so the flag resolves under
            // wildcards. Server operators grant "sendproxy:example" (or "*") via their real admin source.
            registry.RegisterPermissions([SendProxyPermission]);

            registry.RegisterAdminCommand("sp_example_fakehp",         OnFakeHp,         [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_example_fakehp_entity",  OnFakeHpEntity,   [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_example_perclienthp",    OnPerClientHp,    [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_example_perclienthp_off", OnPerClientHpOff, [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_example_fakeangle",      OnFakeAngle,      [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_example_fakeangle_off",  OnFakeAngleOff,   [SendProxyPermission]);
            registry.RegisterAdminCommand("sp_example_off",            OnExampleOff,     [SendProxyPermission]);

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

    #endregion

    #region SendProxy demo commands

    private void OnFakeHp(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is null)
        {
            return;
        }

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var value))
        {
            Reply(issuer, "usage: sp_example_fakehp <value>");

            return;
        }

        _sendProxy.SetUniformInt("CCSPlayerPawn", "m_iHealth", value);
        Reply(issuer, $"Uniform fakehp: CCSPlayerPawn::m_iHealth → {value} for all clients (real HP unchanged)");
    }

    private void OnFakeHpEntity(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is null)
        {
            return;
        }

        if (command.ArgCount < 2
            || !int.TryParse(command.GetArg(1), out var entity)
            || !int.TryParse(command.GetArg(2), out var fake))
        {
            Reply(issuer, "usage: sp_example_fakehp_entity <entityIndex> <fakeValue>");

            return;
        }

        _sendProxy.SetEntitySpoof(entity, "CCSPlayerPawn", "m_iHealth", fake);
        Reply(issuer, $"Per-entity m_iHealth spoof installed: ent={entity} → {fake} (other entities unaffected)");
    }

    private void OnPerClientHp(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is null)
        {
            return;
        }

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var baseValue))
        {
            Reply(issuer, "usage: sp_example_perclienthp <baseValue>");

            return;
        }

        // Capture baseValue by value — the callback runs on engine send threads and must not block or
        // allocate. Demo logic: derive a per-client toggle from the client pointer (no deref).
        var capturedBase = baseValue;
        _sendProxy.HookInt("CCSPlayerPawn", "m_iHealth", (nint client, int entityIndex, ref int value) =>
        {
            value = (client & 1L) != 0 ? capturedBase : capturedBase + 50;

            return true;
        });

        Reply(issuer, $"Per-client m_iHealth hook installed: odd-client-ptr → {capturedBase}, even → {capturedBase + 50}");
    }

    private void OnPerClientHpOff(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is null)
        {
            return;
        }

        _sendProxy.UnhookInt("CCSPlayerPawn", "m_iHealth");
        Reply(issuer, "Per-client m_iHealth hook removed");
    }

    private void OnFakeAngle(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is null)
        {
            return;
        }

        var pitch = 0f;
        var yaw   = 0f;
        var roll  = 0f;
        if (command.ArgCount >= 1)
        {
            float.TryParse(command.GetArg(1), out pitch);
        }

        if (command.ArgCount >= 2)
        {
            float.TryParse(command.GetArg(2), out yaw);
        }

        if (command.ArgCount >= 3)
        {
            float.TryParse(command.GetArg(3), out roll);
        }

        var capturedPitch = pitch;
        var capturedYaw   = yaw;
        var capturedRoll  = roll;

        _sendProxy.HookVector("CCSPlayerPawn", "m_angEyeAngles",
            (nint client, int entityIndex, ref float x, ref float y, ref float z) =>
            {
                x = capturedPitch;
                y = capturedYaw;
                z = capturedRoll;

                return true;
            });

        Reply(issuer, $"Fake eye-angle hook installed: pitch={capturedPitch} yaw={capturedYaw} roll={capturedRoll} (all CCSPlayerPawn)");
    }

    private void OnFakeAngleOff(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is null)
        {
            return;
        }

        _sendProxy.Unhook("CCSPlayerPawn", "m_angEyeAngles");
        Reply(issuer, "Fake eye-angle hook removed");
    }

    private void OnExampleOff(IGameClient? issuer, StringCommand command)
    {
        if (_sendProxy is null)
        {
            return;
        }

        _sendProxy.UnhookAllPerClient();
        Reply(issuer, "sp_example_off: all example hooks cleared, substitution detours uninstalled");
    }

    #endregion

    #region Serializer probe commands

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
}
