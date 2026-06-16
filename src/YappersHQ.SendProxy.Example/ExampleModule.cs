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
using Sharp.Modules.TargetingManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using YappersHQ.SendProxy.Example.Commands;
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

    private static readonly string ModuleIdentity =
        typeof(ExampleModule).Assembly.GetName().Name ?? "YappersHQ.SendProxy.Example";

    private readonly ILogger<ExampleModule>  _logger;
    private readonly ISharpModuleManager     _modules;

    private IModSharpModuleInterface<ISendProxyManager>? _sendProxyHandle;
    private ISendProxyManager?                           _sendProxy => _sendProxyHandle?.Instance;

    private IModSharpModuleInterface<IAdminManager>? _adminManager;
    private bool                                     _commandsRegistered;

    private ExampleContext?        _ctx;
    private ISpCommandCategory[]   _categories = [];

    public ExampleModule(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        _logger  = sharedSystem.GetLoggerFactory().CreateLogger<ExampleModule>();
        _modules = sharedSystem.GetSharpModuleManager();

        _ctx = new ExampleContext(
            sharedSystem.GetEntityManager(),
            sharedSystem.GetClientManager(),
            sharedSystem.GetModSharp(),
            _logger);
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
        _sendProxyHandle = _modules.GetOptionalSharpModuleInterface<ISendProxyManager>(ISendProxyManager.Identity);

        var targeting = _modules.GetOptionalSharpModuleInterface<ITargetingManager>(ITargetingManager.Identity);

        _ctx!.Handle    = _sendProxyHandle;
        _ctx.Targeting  = targeting;

        if (_sendProxy is null)
        {
            _logger.LogWarning("SendProxy not loaded — example disabled");

            return;
        }

        _categories =
        [
            new GenericCommands(_ctx),
            new PresetCommands(_ctx),
            new EncoderDemoCommands(_ctx),
            new FakeAimCommands(_ctx),
            new ProbeCommands(_ctx),
        ];

        TryRegisterCommands();
    }

    public void Shutdown()
    {
        foreach (var c in _categories)
        {
            c.Unregister();
        }

        _sendProxy?.UnhookAll();
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

            // RegisterAdminCommand does not auto-register the permission; do it so the flag resolves under
            // wildcards. Operators grant "sendproxy:example" (or "*") via their real admin source.
            registry.RegisterPermissions([ExampleContext.Permission]);

            foreach (var c in _categories)
            {
                c.Register(registry);
            }

            _commandsRegistered = true;
            _logger.LogInformation("SendProxy example admin commands registered under \"{Perm}\"", ExampleContext.Permission);
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
}
