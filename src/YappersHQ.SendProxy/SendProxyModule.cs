using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

public sealed class SendProxyModule : IModSharpModule, IEntityListener
{
    public string DisplayName   => "SendProxy";
    public string DisplayAuthor => "Prefix";

    private readonly ILogger<SendProxyModule> _logger;
    private readonly InterfaceBridge _bridge;
    private readonly SendProxyManager _manager;

    private nint _encodeFieldAddr;  // CFlattenedSerializer::EncodeField, resolved via sig on load
    private nint _intEncoderAddr;   // CFlattenedSerializer::EncodeInt32, resolved via sig on load

    public SendProxyModule(
        ISharedSystem  sharedSystem,
        string?        dllPath,
        string?        sharpPath,
        Version?       version,
        IConfiguration? coreConfiguration,
        bool           hotReload)
    {
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<SendProxyModule>();
        _bridge = new InterfaceBridge(this, sharedSystem, sharpPath ?? string.Empty);
        _manager = new SendProxyManager(_logger);
    }

    public bool Init()
    {
        // Loads gamedata/yappershq.sendproxy.jsonc (encode-path offsets/sigs). Non-fatal: the
        // string-anchor resolution below doesn't depend on it, so a missing/invalid gamedata
        // must not block module load.
        try
        {
            _bridge.GameData.Register("yappershq.sendproxy");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SendProxy gamedata register failed (continuing without it)");
        }

        ResolveNativeTargets();

        // Read-only diagnostic: dump an entity's serializer layout (entity vtable[0] ->
        // CNetworkSerializerClassInfo) to confirm runtime offsets before the patch is wired.
        _bridge.ConVarManager.CreateServerCommand("sp_dump", OnDumpCommand,
            "Read-only: dump an entity's network serializer layout: sp_dump <entityIndex>",
            ConVarFlags.Release);

        // Read-only field-record dumper: sp_field <serializerClass> <fieldName>. Walks a live entity's
        // serializer field array, dumps the qword window around the target field (resolves the field+0x38
        // encoder-vs-offset question before any swap). e.g. sp_field CCSPlayerPawn m_iHealth
        _bridge.ConVarManager.CreateServerCommand("sp_field", OnFieldCommand,
            "Read-only: dump a serializer field record: sp_field <class> <fieldName>", ConVarFlags.Release);

        // Read-only EncodeField detour probe (manual on/off — logs the real args of the first calls).
        _bridge.ConVarManager.CreateServerCommand("sp_detour_on", OnDetourOn,
            "Install the read-only EncodeField detour probe", ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_detour_off", OnDetourOff,
            "Uninstall the EncodeField detour probe", ConVarFlags.Release);

        // Read-only int32 encoder probe: logs the first 12 m_iHealth encodes to reveal which
        // argument register carries the spoofable value (rdx, rcx, or r8d). No value is modified.
        _bridge.ConVarManager.CreateServerCommand("sp_encprobe", OnEncProbeOn,
            "Install the read-only IntEncoder detour probe (logs m_iHealth encodes)", ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_encprobe_off", OnEncProbeOff,
            "Uninstall the IntEncoder detour probe", ConVarFlags.Release);

        if (!EncoderHook.Enabled)
            _logger.LogWarning(
                "SendProxy loaded in REGISTRATION-ONLY mode — the live encoder patch is disabled until "
                + "the flattened-serializer offsets are verified on this build. Hooks register but values "
                + "are not yet substituted. See README.");

        return true;
    }

    // Gamedata keys. Sigs live in .assets/gamedata/yappershq.sendproxy.jsonc (makesig-derived,
    // see tools/) — NOT hardcoded here, so a game-update only touches the json.
    private const string EncodeFieldKey       = "CFlattenedSerializer::EncodeField";
    private const string EncodeInt32Key       = "CFlattenedSerializer::EncodeInt32";
    private const string SendClientMessagesKey = "CNetworkGameServer::SendClientMessages";

    /// <summary>
    ///     Resolve the encode-path functions from gamedata (single source of truth — sigs are in
    ///     <c>gamedata/yappershq.sendproxy.jsonc</c>, generated with the makesig tooling in <c>tools/</c>).
    ///     Touches no memory; just records addresses for the Phase-1 detour probe.
    /// </summary>
    private void ResolveNativeTargets()
    {
        _encodeFieldAddr = ResolveFromGameData(EncodeFieldKey);
        _intEncoderAddr  = ResolveFromGameData(EncodeInt32Key);
        ResolveFromGameData(SendClientMessagesKey); // Phase-2 anchor; logged for verification only.
        // WriteDeltaEntity_Internal is Phase-2-only and not yet in gamedata (see the json + RE doc).
    }

    // GetAddress throws KeyNotFoundException if the entry didn't resolve (sig miss / not registered).
    // Isolate it so a single miss doesn't abort load — this is read-only groundwork.
    private nint ResolveFromGameData(string key)
    {
        try
        {
            var addr = _bridge.GameData.GetAddress(key);
            _logger.LogInformation("SendProxy resolve {Key} (gamedata): fn=0x{Fn:X}", key, addr);
            return addr;
        }
        catch (Exception e)
        {
            _logger.LogWarning("SendProxy: {Key} not resolved from gamedata: {Msg}", key, e.Message);
            return 0;
        }
    }

    public void PostInit()
    {
        _bridge.SharpModuleManager.RegisterSharpModuleInterface<ISendProxyManager>(
            this, ISendProxyManager.Identity, _manager);

        // Drop hooks when an entity is deleted — its index gets reused (flaw #2 fix).
        _bridge.EntityManager.InstallEntityListener(this);
    }

    // IEntityListener (other members use the interface's default no-op impls).
    int IEntityListener.ListenerVersion => IEntityListener.ApiVersion;
    int IEntityListener.ListenerPriority => 0;
    void IEntityListener.OnEntityDeleted(IBaseEntity entity) => _manager.RemoveEntityHooks((int) entity.Index);

    public void Shutdown()
    {
        EncodeFieldDetour.Uninstall();
        IntEncoderDetour.Uninstall();
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ConVarManager.ReleaseCommand("sp_dump");
        _bridge.ConVarManager.ReleaseCommand("sp_field");
        _bridge.ConVarManager.ReleaseCommand("sp_detour_on");
        _bridge.ConVarManager.ReleaseCommand("sp_detour_off");
        _bridge.ConVarManager.ReleaseCommand("sp_encprobe");
        _bridge.ConVarManager.ReleaseCommand("sp_encprobe_off");
        _manager.Clear();
    }

    private ECommandAction OnDumpCommand(StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            SerializerProbe.Scan(_bridge, _logger); // no arg → scan for live entities + dump the first
            return ECommandAction.Stopped;
        }

        if (!int.TryParse(command.GetArg(1), out var idx))
        {
            _logger.LogInformation("usage: sp_dump [entityIndex]  (no arg = scan)");
            return ECommandAction.Stopped;
        }

        SerializerProbe.Dump(_bridge, _logger, idx);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnFieldCommand(StringCommand command)
    {
        if (command.ArgCount < 2)
        {
            _logger.LogInformation("usage: sp_field <serializerClass> <fieldName>  (e.g. sp_field CCSPlayerPawn m_iHealth)");
            return ECommandAction.Stopped;
        }

        SerializerProbe.DumpField(_bridge, _logger, command.GetArg(1), command.GetArg(2));
        return ECommandAction.Stopped;
    }

    private ECommandAction OnDetourOn(StringCommand command)
    {
        // UNSAFE: detouring EncodeField's *entry* CRASHES the server. EncodeField reads stack args
        // (prologue: mov 0x38(%rbp),...), but this probe is a 6-arg cdecl passthrough — the trampoline
        // call doesn't preserve the stack args, so the original reads garbage and crashes after a few
        // calls (confirmed live 2026-06-14, ~8 hits then exit). It already served its purpose: proved
        // managed interception + revealed the args (b/rsi=0, c/rdx-d/rcx=0x50). The real hook is the
        // per-field encoder at field+0x38 (5 register args, no stack args) — see README/RE doc.
        // Gated behind an explicit "force" arg so it can't be tripped accidentally.
        if (command.ArgCount < 1 || command.GetArg(1) != "force")
        {
            _logger.LogWarning(
                "sp_detour_on is DISABLED — the EncodeField-entry detour crashes the server (stack args "
                + "not preserved by the 6-arg passthrough). Use 'sp_detour_on force' only on a throwaway "
                + "server. The real value hook targets field+0x38, not EncodeField's entry.");
            return ECommandAction.Stopped;
        }

        if (_encodeFieldAddr == 0)
            _logger.LogWarning("sp_detour_on: EncodeField address not resolved — cannot install");
        else
            EncodeFieldDetour.Install(_bridge, _logger, _encodeFieldAddr);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnDetourOff(StringCommand command)
    {
        EncodeFieldDetour.Uninstall();
        return ECommandAction.Stopped;
    }

    private ECommandAction OnEncProbeOn(StringCommand command)
    {
        if (_intEncoderAddr == 0)
            _logger.LogWarning("sp_encprobe: EncodeInt32 address not resolved — cannot install");
        else
            IntEncoderDetour.Install(_bridge, _logger, _intEncoderAddr);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnEncProbeOff(StringCommand command)
    {
        IntEncoderDetour.Uninstall();
        return ECommandAction.Stopped;
    }
}
