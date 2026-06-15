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
            "Spoof m_iHealth for an entity (all-entity scope): sp_example_fakehp <entityIndex> <fakeValue>",
            ConVarFlags.Release);

        // Per-entity Phase-2 demo: uniform value for ONE specific entity, all clients see it.
        _conVar.CreateServerCommand("sp_example_fakehp_entity", OnFakeHpEntity,
            "Spoof m_iHealth for a SPECIFIC entity only (per-entity Phase-2 scope): "
            + "sp_example_fakehp_entity <entityIndex> <fakeValue>",
            ConVarFlags.Release);

        // Per-client demo: different fake HP per recipient.
        _conVar.CreateServerCommand("sp_example_perclienthp", OnPerClientHp,
            "Demo per-client m_iHealth spoof (CCSPlayerPawn): sp_example_perclienthp <baseValue>. "
            + "Odd-addressed clients see baseValue, even-addressed clients see baseValue+50.",
            ConVarFlags.Release);
        _conVar.CreateServerCommand("sp_example_perclienthp_off", OnPerClientHpOff,
            "Remove the per-client m_iHealth callback registered by sp_example_perclienthp.",
            ConVarFlags.Release);

        // QAngle demo: hook an eye-angle field on CCSPlayerPawn so every client sees a fixed pitch.
        // "m_angEyeAngles" is the CCSPlayerPawn qangle field that replicates the player's view angles.
        // Use sp_example_fakeangle_off to remove.
        _conVar.CreateServerCommand("sp_example_fakeangle", OnFakeAngle,
            "Spoof CCSPlayerPawn::m_angEyeAngles for all clients to a fixed pitch: "
            + "sp_example_fakeangle <pitch> <yaw> <roll>",
            ConVarFlags.Release);
        _conVar.CreateServerCommand("sp_example_fakeangle_off", OnFakeAngleOff,
            "Remove the per-client eye-angle hook installed by sp_example_fakeangle.",
            ConVarFlags.Release);
    }

    public void Shutdown()
    {
        _conVar.ReleaseCommand("sp_example_fakehp");
        _conVar.ReleaseCommand("sp_example_fakehp_entity");
        // Remove per-client callbacks on shutdown to avoid dangling references.
        _sendProxy?.UnhookAllPerClient();
        _conVar.ReleaseCommand("sp_example_perclienthp");
        _conVar.ReleaseCommand("sp_example_perclienthp_off");
        _conVar.ReleaseCommand("sp_example_fakeangle");
        _conVar.ReleaseCommand("sp_example_fakeangle_off");
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

    private ECommandAction OnFakeHpEntity(StringCommand command)
    {
        if (_sendProxy is null)
            return ECommandAction.Stopped;

        if (command.ArgCount < 2
            || !int.TryParse(command.GetArg(1), out var entity)
            || !int.TryParse(command.GetArg(2), out var fake))
        {
            _logger.LogInformation("usage: sp_example_fakehp_entity <entityIndex> <fakeValue>");
            return ECommandAction.Stopped;
        }

        // Scoped to entityIndex only — other players' CCSPlayerPawn m_iHealth is untouched.
        // Every client receives `fake` when reading this specific entity's health.
        // The Phase-2 WDE entity-index capture (_currentEntityIndex == entityIndex) gates substitution.
        _sendProxy.SetEntitySpoof(entity, "CCSPlayerPawn", "m_iHealth", fake);

        _logger.LogInformation(
            "Per-entity m_iHealth spoof installed: ent={Entity} → {Fake} (all other entities unaffected)",
            entity, fake);
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

    private ECommandAction OnFakeAngle(StringCommand command)
    {
        if (_sendProxy is null)
            return ECommandAction.Stopped;

        // Parse pitch/yaw/roll from args (default to 0 0 0).
        float pitch = 0f, yaw = 0f, roll = 0f;
        if (command.ArgCount >= 1) float.TryParse(command.GetArg(1), out pitch);
        if (command.ArgCount >= 2) float.TryParse(command.GetArg(2), out yaw);
        if (command.ArgCount >= 3) float.TryParse(command.GetArg(3), out roll);

        var capturedPitch = pitch;
        var capturedYaw   = yaw;
        var capturedRoll  = roll;

        // Hook CCSPlayerPawn::m_angEyeAngles — a QAngle3 field.
        // Every client will see the same fixed angles for ALL player pawns regardless of who
        // is sending. Real server-side view angles are untouched.
        //
        // For a per-entity version (spoof angles of only one specific pawn) use HookEntityVector.
        _sendProxy.HookVector("CCSPlayerPawn", "m_angEyeAngles",
            (nint client, int entityIndex, ref float x, ref float y, ref float z) =>
            {
                x = capturedPitch;
                y = capturedYaw;
                z = capturedRoll;
                return true; // substitute
            });

        _logger.LogInformation(
            "Fake eye-angle hook installed: pitch={P} yaw={Y} roll={R} (all CCSPlayerPawn, all clients)",
            capturedPitch, capturedYaw, capturedRoll);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnFakeAngleOff(StringCommand command)
    {
        if (_sendProxy is null)
            return ECommandAction.Stopped;

        _sendProxy.Unhook("CCSPlayerPawn", "m_angEyeAngles");
        _logger.LogInformation("Fake eye-angle hook removed");
        return ECommandAction.Stopped;
    }
}
