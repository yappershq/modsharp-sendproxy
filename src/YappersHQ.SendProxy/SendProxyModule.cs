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

    private nint _encodeFieldAddr;    // CFlattenedSerializer::EncodeField, resolved via sig on load
    private nint _intEncoderAddr;    // CFlattenedSerializer::EncodeInt32, resolved via sig on load
    private nint _wdeAddr;           // CNetworkGameServerBase::WriteDeltaEntity_Internal, resolved via sig on load
    private nint _perClientEncodeAddr; // CNetworkGameServer::PerClientEncode, resolved via sig on load
    private nint _writeFieldListAddr; // CFlattenedSerializer::WriteFieldList, resolved via sig on load
    private nint _getBitRangeAddr;   // CFlattenedSerializer::GetBitRange, resolved via sig on load
    private nint _bitCopyAddr;       // FUN_00500b70 (bit-copy primitive), resolved via sig on load
    private nint _varintWriterAddr;  // FUN_00500890 (zigzag/varint writer), resolved via sig on load

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

        // Read-only WriteDeltaEntity_Internal probe: logs first 40 calls (thread id + arg layout)
        // to confirm serial per-client execution and reveal ctx field offsets for Phase-2 per-client spoofing.
        _bridge.ConVarManager.CreateServerCommand("sp_wdeprobe", OnWdeProbeOn,
            "Install the read-only WriteDeltaEntity_Internal detour probe (logs first 40 calls with thread id + ctx fields)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_wdeprobe_off", OnWdeProbeOff,
            "Uninstall the WriteDeltaEntity_Internal detour probe", ConVarFlags.Release);

        // Per-client encode entry probe: sp_recipcap (on) / sp_recipcap_off (off).
        // Detours CNetworkGameServer::PerClientEncode; captures rsi (CServerSideClient*) into a
        // [ThreadStatic] and logs the first 30 calls to confirm: one call per client, on worker
        // threads, with a stable non-null pointer. Pure passthrough — no values modified.
        _bridge.ConVarManager.CreateServerCommand("sp_recipcap", OnRecipCapOn,
            "Install the per-client encode entry probe (logs first 30 calls: tid + client ptr)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_recipcap_off", OnRecipCapOff,
            "Uninstall the per-client encode entry probe", ConVarFlags.Release);

        // VERIFY-MODE WriteFieldList probe: sp_wflprobe (on) / sp_wflprobe_off (off).
        // Detours CFlattenedSerializer::WriteFieldList; logs first 30 calls with thread id,
        // RecipientCapture.CurrentClient, serializer name, first field name, changed-field count,
        // and dst bf_write ptr.  Pure passthrough — no values modified.
        // Confirms: (a) the captured client ptr is visible inside WFL, (b) field identity is
        // readable from param_1 (the CFlattenedSerializer* in rdi), (c) which threads execute WFL.
        _bridge.ConVarManager.CreateServerCommand("sp_wflprobe", OnWflProbeOn,
            "Install the VERIFY-MODE WriteFieldList probe (logs first 30 calls: tid + client + serializer + field)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_wflprobe_off", OnWflProbeOff,
            "Uninstall the WriteFieldList probe", ConVarFlags.Release);

        // Live value spoof: sp_fakehp <value> — makes all clients see fake HP; server keeps real.
        _bridge.ConVarManager.CreateServerCommand("sp_fakehp", OnFakeHp,
            "Spoof m_iHealth for all clients: sp_fakehp <value>", ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_fakehp_off", OnFakeHpOff,
            "Stop spoofing m_iHealth", ConVarFlags.Release);

        // Phase-2 per-client per-field substitution (WFL-level, bit-stream rewrite).
        // sp_sub_verify — install all 3 sub-detours + register m_iHealth, mode=Verify (read-only logging).
        // sp_fakehp2 <value> — set mode=Fake + register m_iHealth→value for all clients.
        // sp_sub_off — mode=Off + clear registry + uninstall detours.
        _bridge.ConVarManager.CreateServerCommand("sp_sub_verify", OnSubVerify,
            "Install Phase-2 sub-detours in VERIFY mode (read-only, logs cursor math for m_iHealth)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_fakehp2", OnFakeHp2,
            "Phase-2 per-field spoof: sp_fakehp2 <value> — all clients see fake m_iHealth",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_sub_off", OnSubOff,
            "Disable Phase-2 field substitution, clear registry, uninstall sub-detours",
            ConVarFlags.Release);

        if (!EncoderHook.Enabled)
            _logger.LogWarning(
                "SendProxy loaded in REGISTRATION-ONLY mode — the live encoder patch is disabled until "
                + "the flattened-serializer offsets are verified on this build. Hooks register but values "
                + "are not yet substituted. See README.");

        return true;
    }

    // Gamedata keys. Sigs live in .assets/gamedata/yappershq.sendproxy.jsonc (makesig-derived,
    // see tools/) — NOT hardcoded here, so a game-update only touches the json.
    private const string EncodeFieldKey        = "CFlattenedSerializer::EncodeField";
    private const string EncodeInt32Key        = "CFlattenedSerializer::EncodeInt32";
    private const string SendClientMessagesKey  = "CNetworkGameServer::SendClientMessages";
    private const string WriteDeltaInternalKey  = "CNetworkGameServerBase::WriteDeltaEntity_Internal";
    private const string PerClientEncodeKey     = "CNetworkGameServer::PerClientEncode";
    private const string WriteFieldListKey      = "CFlattenedSerializer::WriteFieldList";
    private const string GetBitRangeKey        = "CFlattenedSerializer::GetBitRange";
    private const string BitCopyKey            = "CFlattenedSerializer::BitCopyPrimitive";
    private const string VarintWriterKey       = "CFlattenedSerializer::VarintWriter";

    /// <summary>
    ///     Resolve the encode-path functions from gamedata (single source of truth — sigs are in
    ///     <c>gamedata/yappershq.sendproxy.jsonc</c>, generated with the makesig tooling in <c>tools/</c>).
    ///     Touches no memory; just records addresses for the Phase-1 detour probe.
    /// </summary>
    private void ResolveNativeTargets()
    {
        _encodeFieldAddr    = ResolveFromGameData(EncodeFieldKey);
        _intEncoderAddr     = ResolveFromGameData(EncodeInt32Key);
        ResolveFromGameData(SendClientMessagesKey); // Phase-2 anchor; logged for verification only.
        _wdeAddr            = ResolveFromGameData(WriteDeltaInternalKey);
        _perClientEncodeAddr = ResolveFromGameData(PerClientEncodeKey);
        _writeFieldListAddr = ResolveFromGameData(WriteFieldListKey);
        _getBitRangeAddr    = ResolveFromGameData(GetBitRangeKey);
        _bitCopyAddr        = ResolveFromGameData(BitCopyKey);
        _varintWriterAddr   = ResolveFromGameData(VarintWriterKey);
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
        // Wire the Phase-2 detour installer into SendProxyManager so HookInt(ser,field,cb) can
        // lazily install sub-detours without needing a direct reference to SendProxyModule.
        _manager.SetSubDetourInstaller(EnsureSubDetours);

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
        WriteDeltaProbe.Uninstall();
        RecipientCapture.Uninstall();
        WriteFieldProbe.Uninstall();
        FieldSubstitution.Uninstall();
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ConVarManager.ReleaseCommand("sp_dump");
        _bridge.ConVarManager.ReleaseCommand("sp_field");
        _bridge.ConVarManager.ReleaseCommand("sp_detour_on");
        _bridge.ConVarManager.ReleaseCommand("sp_detour_off");
        _bridge.ConVarManager.ReleaseCommand("sp_encprobe");
        _bridge.ConVarManager.ReleaseCommand("sp_encprobe_off");
        _bridge.ConVarManager.ReleaseCommand("sp_wdeprobe");
        _bridge.ConVarManager.ReleaseCommand("sp_wdeprobe_off");
        _bridge.ConVarManager.ReleaseCommand("sp_recipcap");
        _bridge.ConVarManager.ReleaseCommand("sp_recipcap_off");
        _bridge.ConVarManager.ReleaseCommand("sp_wflprobe");
        _bridge.ConVarManager.ReleaseCommand("sp_wflprobe_off");
        _bridge.ConVarManager.ReleaseCommand("sp_fakehp");
        _bridge.ConVarManager.ReleaseCommand("sp_fakehp_off");
        _bridge.ConVarManager.ReleaseCommand("sp_sub_verify");
        _bridge.ConVarManager.ReleaseCommand("sp_fakehp2");
        _bridge.ConVarManager.ReleaseCommand("sp_sub_off");
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

    private ECommandAction OnFakeHp(StringCommand command)
    {
        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var value))
        {
            _logger.LogInformation("usage: sp_fakehp <value>");
            return ECommandAction.Stopped;
        }

        if (_intEncoderAddr == 0)
        {
            _logger.LogWarning("sp_fakehp: EncodeInt32 address not resolved — cannot install detour");
            return ECommandAction.Stopped;
        }

        IntEncoderDetour.Install(_bridge, _logger, _intEncoderAddr);
        IntEncoderDetour.SetSpoof("m_iHealth", value);
        _logger.LogInformation("fakehp: m_iHealth -> {Value} for all clients (real HP unchanged)", value);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnFakeHpOff(StringCommand command)
    {
        IntEncoderDetour.ClearSpoof("m_iHealth");
        if (!IntEncoderDetour.HasSpoofs)
            IntEncoderDetour.Uninstall();
        _logger.LogInformation("fakehp off");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWdeProbeOn(StringCommand command)
    {
        if (_wdeAddr == 0)
            _logger.LogWarning("sp_wdeprobe: WriteDeltaEntity_Internal address not resolved — cannot install");
        else
            WriteDeltaProbe.Install(_bridge, _logger, _wdeAddr);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWdeProbeOff(StringCommand command)
    {
        WriteDeltaProbe.Uninstall();
        return ECommandAction.Stopped;
    }

    private ECommandAction OnRecipCapOn(StringCommand command)
    {
        if (_perClientEncodeAddr == 0)
            _logger.LogWarning("sp_recipcap: PerClientEncode address not resolved — cannot install");
        else
            RecipientCapture.Install(_bridge, _logger, _perClientEncodeAddr);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnRecipCapOff(StringCommand command)
    {
        RecipientCapture.Uninstall();
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWflProbeOn(StringCommand command)
    {
        if (_writeFieldListAddr == 0)
            _logger.LogWarning("sp_wflprobe: WriteFieldList address not resolved — cannot install");
        else
            WriteFieldProbe.Install(_bridge, _logger, _writeFieldListAddr);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWflProbeOff(StringCommand command)
    {
        WriteFieldProbe.Uninstall();
        return ECommandAction.Stopped;
    }

    // ── Phase-2 field substitution commands ───────────────────────────────────────────────────

    private bool EnsureSubDetours()
    {
        if (_getBitRangeAddr == 0 || _bitCopyAddr == 0 || _varintWriterAddr == 0 || _writeFieldListAddr == 0)
        {
            _logger.LogWarning(
                "sp_sub_*: one or more Phase-2 addresses not resolved "
                + "(GetBitRange={Gbr:X} BitCopy={Bc:X} VarintWriter={Vw:X} WFL={Wfl:X}) — "
                + "check gamedata sigs match this build",
                _getBitRangeAddr, _bitCopyAddr, _varintWriterAddr, _writeFieldListAddr);
            return false;
        }

        // Populate FieldSubstitution address slots (idempotent — values don't change).
        FieldSubstitution.GetBitRangeAddr    = _getBitRangeAddr;
        FieldSubstitution.ValueCopyAddr      = _bitCopyAddr;
        FieldSubstitution.VarintWriterAddr   = _varintWriterAddr;
        FieldSubstitution.WriteFieldListAddr = _writeFieldListAddr;
        // WDE address for entity-index capture (optional; 0 = skip, entityIndex in callbacks will be -1).
        FieldSubstitution.WdeAddr            = _wdeAddr;

        // Also ensure RecipientCapture is running so CurrentClient is valid during substitution.
        if (_perClientEncodeAddr != 0)
            RecipientCapture.Install(_bridge, _logger, _perClientEncodeAddr);

        return FieldSubstitution.Install(_bridge, _logger);
    }

    private ECommandAction OnSubVerify(StringCommand command)
    {
        if (!EnsureSubDetours())
            return ECommandAction.Stopped;

        // Register m_iHealth on CCSPlayerPawn as the canary field.
        FieldSubstitution.SetSpoof("CCSPlayerPawn", "m_iHealth", 0 /* ignored in Verify */);
        FieldSubstitution.Mode = SubstitutionMode.Verify;
        _logger.LogInformation(
            "Phase-2 VERIFY mode active — watching CCSPlayerPawn::m_iHealth. "
            + "Check logs for SUBST-VERIFY lines (cursor math, zero output change).");
        return ECommandAction.Stopped;
    }

    private ECommandAction OnFakeHp2(StringCommand command)
    {
        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var value))
        {
            _logger.LogInformation("usage: sp_fakehp2 <value>");
            return ECommandAction.Stopped;
        }

        if (!EnsureSubDetours())
            return ECommandAction.Stopped;

        FieldSubstitution.SetSpoof("CCSPlayerPawn", "m_iHealth", value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation(
            "Phase-2 FAKE mode: CCSPlayerPawn::m_iHealth → {Value} for all clients (server HP unchanged)",
            value);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnSubOff(StringCommand command)
    {
        FieldSubstitution.ClearSpoofs();
        FieldSubstitution.ClearCallbacks();
        FieldSubstitution.Uninstall();
        _logger.LogInformation("Phase-2 field substitution OFF — all sub-detours uninstalled");
        return ECommandAction.Stopped;
    }
}
