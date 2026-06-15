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

    private nint _encodeFieldAddr;     // CFlattenedSerializer::EncodeField
    private nint _intEncoderAddr;      // CFlattenedSerializer::EncodeInt32
    private nint _wdeAddr;             // CNetworkGameServerBase::WriteDeltaEntity_Internal
    private nint _perClientEncodeAddr; // CNetworkGameServer::PerClientEncode
    private nint _writeFieldListAddr;  // CFlattenedSerializer::WriteFieldList
    private nint _getBitRangeAddr;     // CFlattenedSerializer::GetBitRange
    private nint _bitCopyAddr;         // FUN_00500b70 (bit-copy primitive)
    private nint _varintWriterAddr;    // FUN_00500890 (zigzag/varint writer)
    private nint _encUInt32Addr;       // EncodeUInt32 identity (raw varint, no zigzag)
    private nint _encFloat32Addr;      // EncodeFloat32 identity (32-bit inline write)
    private nint _encBoolAddr;         // EncodeBool identity (1-bit inline write)

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
        // Loads gamedata/yappershq.sendproxy.jsonc (encode-path sigs). Non-fatal: a missing/invalid
        // gamedata must not block module load — ResolveNativeTargets handles per-key failures.
        try
        {
            _bridge.GameData.Register("yappershq.sendproxy");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SendProxy gamedata register failed (continuing without it)");
        }

        ResolveNativeTargets();

        // Read-only diagnostics: dump serializer layout + field record windows.
        _bridge.ConVarManager.CreateServerCommand("sp_dump", OnDumpCommand,
            "Dump an entity's network serializer layout: sp_dump <entityIndex>",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_field", OnFieldCommand,
            "Dump a serializer field record: sp_field <class> <fieldName>",
            ConVarFlags.Release);

        // Probes (read-only, install/uninstall pairs).
        _bridge.ConVarManager.CreateServerCommand("sp_encprobe", OnEncProbeOn,
            "Install the read-only IntEncoder detour probe (logs m_iHealth encodes)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_encprobe_off", OnEncProbeOff,
            "Uninstall the IntEncoder detour probe", ConVarFlags.Release);

        _bridge.ConVarManager.CreateServerCommand("sp_wdeprobe", OnWdeProbeOn,
            "Install the read-only WriteDeltaEntity_Internal probe (logs first 40 calls)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_wdeprobe_off", OnWdeProbeOff,
            "Uninstall the WriteDeltaEntity_Internal probe", ConVarFlags.Release);

        // RecipientCapture: detours PerClientEncode; captures CServerSideClient* per send thread.
        _bridge.ConVarManager.CreateServerCommand("sp_recipcap", OnRecipCapOn,
            "Install the per-client encode entry probe (logs first 30 calls: tid + client ptr)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_recipcap_off", OnRecipCapOff,
            "Uninstall the per-client encode entry probe", ConVarFlags.Release);

        // WriteFieldList probe: verifies client ptr + serializer identity inside WFL.
        _bridge.ConVarManager.CreateServerCommand("sp_wflprobe", OnWflProbeOn,
            "Install the WriteFieldList probe (logs first 30 calls: tid + client + serializer + field)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_wflprobe_off", OnWflProbeOff,
            "Uninstall the WriteFieldList probe", ConVarFlags.Release);

        // Phase-1 uniform spoof via IntEncoderDetour (all clients, field name keyed).
        _bridge.ConVarManager.CreateServerCommand("sp_fakehp", OnFakeHp,
            "Spoof m_iHealth for all clients: sp_fakehp <value>", ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_fakehp_off", OnFakeHpOff,
            "Stop spoofing m_iHealth", ConVarFlags.Release);

        // Phase-2 per-client WFL-level substitution.
        _bridge.ConVarManager.CreateServerCommand("sp_sub_verify", OnSubVerify,
            "Install Phase-2 sub-detours in VERIFY mode (read-only, logs cursor math for m_iHealth)",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_fakehp2", OnFakeHp2,
            "Phase-2 per-field spoof: sp_fakehp2 <value> — all clients see fake m_iHealth",
            ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_sub_off", OnSubOff,
            "Disable Phase-2 field substitution, clear registry, uninstall sub-detours",
            ConVarFlags.Release);

        return true;
    }

    // Gamedata keys — sigs live in .assets/gamedata/yappershq.sendproxy.jsonc.
    private const string EncodeFieldKey        = "CFlattenedSerializer::EncodeField";
    private const string EncodeInt32Key        = "CFlattenedSerializer::EncodeInt32";
    private const string SendClientMessagesKey  = "CNetworkGameServer::SendClientMessages";
    private const string WriteDeltaInternalKey  = "CNetworkGameServerBase::WriteDeltaEntity_Internal";
    private const string PerClientEncodeKey     = "CNetworkGameServer::PerClientEncode";
    private const string WriteFieldListKey      = "CFlattenedSerializer::WriteFieldList";
    private const string GetBitRangeKey        = "CFlattenedSerializer::GetBitRange";
    private const string BitCopyKey            = "CFlattenedSerializer::BitCopyPrimitive";
    private const string VarintWriterKey       = "CFlattenedSerializer::VarintWriter";
    private const string EncodeUInt32Key       = "CFlattenedSerializer::EncodeUInt32";
    private const string EncodeFloat32Key      = "CFlattenedSerializer::EncodeFloat32";
    private const string EncodeBoolKey         = "CFlattenedSerializer::EncodeBool";

    private void ResolveNativeTargets()
    {
        _encodeFieldAddr    = ResolveFromGameData(EncodeFieldKey);
        _intEncoderAddr     = ResolveFromGameData(EncodeInt32Key);
        ResolveFromGameData(SendClientMessagesKey); // Phase-2 anchor; logged for verification.
        _wdeAddr            = ResolveFromGameData(WriteDeltaInternalKey);
        _perClientEncodeAddr = ResolveFromGameData(PerClientEncodeKey);
        _writeFieldListAddr = ResolveFromGameData(WriteFieldListKey);
        _getBitRangeAddr    = ResolveFromGameData(GetBitRangeKey);
        _bitCopyAddr        = ResolveFromGameData(BitCopyKey);
        _varintWriterAddr   = ResolveFromGameData(VarintWriterKey);
        // Encoder-identity probes for per-field-type classification (not called; compared against
        // the field's encoder fn ptr). A failed resolve only disables that type → passthrough.
        _encUInt32Addr      = ResolveFromGameData(EncodeUInt32Key);
        _encFloat32Addr     = ResolveFromGameData(EncodeFloat32Key);
        _encBoolAddr        = ResolveFromGameData(EncodeBoolKey);
    }

    private nint ResolveFromGameData(string key)
    {
        try
        {
            var addr = _bridge.GameData.GetAddress(key);
            _logger.LogInformation("SendProxy resolve {Key}: fn=0x{Fn:X}", key, addr);
            return addr;
        }
        catch (Exception e)
        {
            _logger.LogWarning("SendProxy: {Key} not resolved: {Msg}", key, e.Message);
            return 0;
        }
    }

    public void PostInit()
    {
        _manager.SetSubDetourInstaller(EnsureSubDetours);

        _bridge.SharpModuleManager.RegisterSharpModuleInterface<ISendProxyManager>(
            this, ISendProxyManager.Identity, _manager);

        _bridge.EntityManager.InstallEntityListener(this);
    }

    int IEntityListener.ListenerVersion  => IEntityListener.ApiVersion;
    int IEntityListener.ListenerPriority => 0;
    void IEntityListener.OnEntityDeleted(IBaseEntity entity) => _manager.RemoveEntityHooks((int) entity.Index);

    public void Shutdown()
    {
        IntEncoderDetour.Uninstall();
        WriteDeltaProbe.Uninstall();
        RecipientCapture.Uninstall();
        WriteFieldProbe.Uninstall();
        FieldSubstitution.Uninstall();
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ConVarManager.ReleaseCommand("sp_dump");
        _bridge.ConVarManager.ReleaseCommand("sp_field");
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
            SerializerProbe.Scan(_bridge, _logger);
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
            _logger.LogInformation("usage: sp_field <serializerClass> <fieldName>");
            return ECommandAction.Stopped;
        }

        SerializerProbe.DumpField(_bridge, _logger, command.GetArg(1), command.GetArg(2));
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

    // ── Phase-2 field substitution ────────────────────────────────────────────

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

        FieldSubstitution.GetBitRangeAddr    = _getBitRangeAddr;
        FieldSubstitution.ValueCopyAddr      = _bitCopyAddr;
        FieldSubstitution.VarintWriterAddr   = _varintWriterAddr;
        FieldSubstitution.WriteFieldListAddr = _writeFieldListAddr;
        // WDE address for entity-index capture (0 = skip; entityIndex in callbacks will be -1).
        FieldSubstitution.WdeAddr            = _wdeAddr;
        // Encoder-identity addresses for per-field-type classification. EncodeInt32 (signed) reuses
        // the already-resolved _intEncoderAddr. Any 0 here disables that type → passthrough.
        FieldSubstitution.EncSignedAddr      = _intEncoderAddr;
        FieldSubstitution.EncUInt32Addr      = _encUInt32Addr;
        FieldSubstitution.EncFloat32Addr     = _encFloat32Addr;
        FieldSubstitution.EncBoolAddr        = _encBoolAddr;

        if (_perClientEncodeAddr != 0)
            RecipientCapture.Install(_bridge, _logger, _perClientEncodeAddr);

        return FieldSubstitution.Install(_bridge, _logger);
    }

    private ECommandAction OnSubVerify(StringCommand command)
    {
        if (!EnsureSubDetours())
            return ECommandAction.Stopped;

        FieldSubstitution.SetSpoof("CCSPlayerPawn", "m_iHealth", 0);
        FieldSubstitution.Mode = SubstitutionMode.Verify;
        _logger.LogInformation(
            "Phase-2 VERIFY mode active — watching CCSPlayerPawn::m_iHealth. "
            + "Check logs for SUBST-VERIFY lines.");
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
        _logger.LogInformation("Phase-2 field substitution OFF");
        return ECommandAction.Stopped;
    }
}
