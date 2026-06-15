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

    private nint _wdeAddr;             // CNetworkGameServerBase::WriteDeltaEntity_Internal
    private nint _perClientEncodeAddr; // CNetworkGameServer::PerClientEncode
    private nint _writeFieldListAddr;  // CFlattenedSerializer::WriteFieldList
    private nint _getBitRangeAddr;     // CFlattenedSerializer::GetBitRange
    private nint _bitCopyAddr;         // FUN_00500b70 (bit-copy primitive)
    private nint _registryAddr;        // CFlattenedSerializer::EncoderRegistry table base

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

        return true;
    }

    // Gamedata keys — sigs live in .assets/gamedata/yappershq.sendproxy.jsonc.
    private const string SendClientMessagesKey  = "CNetworkGameServer::SendClientMessages";
    private const string WriteDeltaInternalKey  = "CNetworkGameServerBase::WriteDeltaEntity_Internal";
    private const string PerClientEncodeKey     = "CNetworkGameServer::PerClientEncode";
    private const string WriteFieldListKey      = "CFlattenedSerializer::WriteFieldList";
    private const string GetBitRangeKey        = "CFlattenedSerializer::GetBitRange";
    private const string BitCopyKey            = "CFlattenedSerializer::BitCopyPrimitive";
    private const string EncoderRegistryKey    = "CFlattenedSerializer::EncoderRegistry";

    private void ResolveNativeTargets()
    {
        ResolveFromGameData(SendClientMessagesKey); // Phase-2 anchor; logged for verification.
        _wdeAddr            = ResolveFromGameData(WriteDeltaInternalKey);
        _perClientEncodeAddr = ResolveFromGameData(PerClientEncodeKey);
        _writeFieldListAddr = ResolveFromGameData(WriteFieldListKey);
        _getBitRangeAddr    = ResolveFromGameData(GetBitRangeKey);
        _bitCopyAddr        = ResolveFromGameData(BitCopyKey);
        // Encoder registry table base — walked once at Install to build fn→FieldType map.
        _registryAddr       = ResolveFromGameData(EncoderRegistryKey);
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
        RecipientCapture.Uninstall();
        FieldSubstitution.Uninstall();
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ConVarManager.ReleaseCommand("sp_dump");
        _bridge.ConVarManager.ReleaseCommand("sp_field");
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

    // ── Phase-2 field substitution ────────────────────────────────────────────

    private bool EnsureSubDetours()
    {
        if (_getBitRangeAddr == 0 || _bitCopyAddr == 0 || _writeFieldListAddr == 0)
        {
            _logger.LogWarning(
                "SendProxy: one or more Phase-2 addresses not resolved "
                + "(GetBitRange={Gbr:X} BitCopy={Bc:X} WFL={Wfl:X}) — "
                + "check gamedata sigs match this build",
                _getBitRangeAddr, _bitCopyAddr, _writeFieldListAddr);
            return false;
        }

        FieldSubstitution.GetBitRangeAddr    = _getBitRangeAddr;
        FieldSubstitution.ValueCopyAddr      = _bitCopyAddr;
        FieldSubstitution.WriteFieldListAddr = _writeFieldListAddr;
        // WDE address for entity-index capture (0 = skip; entityIndex in callbacks will be -1).
        FieldSubstitution.WdeAddr            = _wdeAddr;
        // Registry table base for runtime fn→FieldType classification. 0 → Classify always returns
        // Unsupported (all substitutions pass through — safe but inert).
        FieldSubstitution.RegistryAddr       = _registryAddr;

        if (_perClientEncodeAddr != 0)
            RecipientCapture.Install(_bridge, _logger, _perClientEncodeAddr);

        return FieldSubstitution.Install(_bridge, _logger);
    }
}
