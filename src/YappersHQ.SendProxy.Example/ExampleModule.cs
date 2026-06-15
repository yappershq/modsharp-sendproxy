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

        // Per-client demo: different fake HP per recipient.
        _conVar.CreateServerCommand("sp_example_perclienthp", OnPerClientHp,
            "Demo per-client m_iHealth spoof (CCSPlayerPawn): sp_example_perclienthp <baseValue>. "
            + "Odd-addressed clients see baseValue, even-addressed clients see baseValue+50.",
            ConVarFlags.Release);
        _conVar.CreateServerCommand("sp_example_perclienthp_off", OnPerClientHpOff,
            "Remove the per-client m_iHealth callback registered by sp_example_perclienthp.",
            ConVarFlags.Release);
    }

    public void Shutdown()
    {
        _conVar.ReleaseCommand("sp_example_fakehp");
        // Remove per-client callback on shutdown to avoid dangling references.
        _sendProxy?.UnhookAllPerClient();
        _conVar.ReleaseCommand("sp_example_perclienthp");
        _conVar.ReleaseCommand("sp_example_perclienthp_off");
    }

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

    private ECommandAction OnPerClientHp(StringCommand command)
    {
        if (_sendProxy is null)
            return ECommandAction.Stopped;

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var baseValue))
        {
            _logger.LogInformation("usage: sp_example_perclienthp <baseValue>");
            return ECommandAction.Stopped;
        }

        // Capture baseValue by value in the lambda — safe for concurrent call from send threads.
        // The callback runs on ~6 engine worker threads; it must not block or allocate.
        //
        // Demo logic: clients at odd-numbered addresses see baseValue; even-addressed see baseValue+50.
        // This is purely illustrative — real usage would key off a slot-indexed player data array.
        var capturedBase = baseValue;
        _sendProxy.HookInt("CCSPlayerPawn", "m_iHealth", (nint client, int entityIndex, ref int value) =>
        {
            // client is a raw CServerSideClient*. We derive a simple per-client toggle from the
            // pointer address — safe to read without dereferencing (ptr arithmetic only).
            value = ((client & 1L) != 0) ? capturedBase : capturedBase + 50;
            return true;  // substitute
        });

        _logger.LogInformation(
            "Per-client m_iHealth hook installed: odd-client-ptr → {Base}, even → {Base2}",
            capturedBase, capturedBase + 50);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnPerClientHpOff(StringCommand command)
    {
        if (_sendProxy is null)
            return ECommandAction.Stopped;

        _sendProxy.UnhookInt("CCSPlayerPawn", "m_iHealth");
        _logger.LogInformation("Per-client m_iHealth hook removed");
        return ECommandAction.Stopped;
    }
}
