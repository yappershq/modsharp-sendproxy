using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Example;

/// <summary>
///     Example consumer of <see cref="ISendProxyManager"/>. Demonstrates the SourceMod-style API:
///     hook an entity's <c>m_iHealth</c> so clients see a fake value while the real health is
///     unchanged. Console: <c>sp_example_fakehp &lt;entityIndex&gt; &lt;fakeValue&gt;</c>.
/// </summary>
public sealed class ExampleModule : IModSharpModule
{
    public string DisplayName   => "SendProxy Example";
    public string DisplayAuthor => "Prefix";

    private readonly ILogger<ExampleModule> _logger;
    private readonly IConVarManager _conVar;
    private readonly ISharpModuleManager _modules;

    private ISendProxyManager? _sendProxy;

    public ExampleModule(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        _logger  = sharedSystem.GetLoggerFactory().CreateLogger<ExampleModule>();
        _conVar  = sharedSystem.GetConVarManager();
        _modules = sharedSystem.GetSharpModuleManager();
    }

    public bool Init() => true;

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

        _conVar.CreateServerCommand("sp_example_fakehp", OnFakeHp,
            "Spoof m_iHealth for an entity: sp_example_fakehp <entityIndex> <fakeValue>",
            ConVarFlags.Release);
    }

    public void Shutdown() => _conVar.ReleaseCommand("sp_example_fakehp");

    private ECommandAction OnFakeHp(StringCommand command)
    {
        if (_sendProxy is null)
            return ECommandAction.Stopped;

        if (command.ArgCount < 2
            || !int.TryParse(command.GetArg(1), out var entity)
            || !int.TryParse(command.GetArg(2), out var fake))
        {
            _logger.LogInformation("usage: sp_example_fakehp <entityIndex> <fakeValue>");
            return ECommandAction.Stopped;
        }

        // Every client will see `fake` for this entity's health; the real m_iHealth is untouched.
        var ok = _sendProxy.HookInt(entity, "m_iHealth", (client, ent, prop, element, ref value) =>
        {
            value = fake;
            return SendProxyResult.Changed;
        });

        _logger.LogInformation("Hooked m_iHealth on entity {Entity} -> {Fake} (registered={Ok})", entity, fake, ok);
        return ECommandAction.Stopped;
    }
}
