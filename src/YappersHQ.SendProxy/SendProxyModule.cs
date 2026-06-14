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

    private nint _encodeFieldAddr; // CFlattenedSerializer::EncodeField, resolved via sig on load

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

        // Read-only EncodeField detour probe (manual on/off — logs the real args of the first calls).
        _bridge.ConVarManager.CreateServerCommand("sp_detour_on", OnDetourOn,
            "Install the read-only EncodeField detour probe", ConVarFlags.Release);
        _bridge.ConVarManager.CreateServerCommand("sp_detour_off", OnDetourOff,
            "Uninstall the EncodeField detour probe", ConVarFlags.Release);

        if (!EncoderHook.Enabled)
            _logger.LogWarning(
                "SendProxy loaded in REGISTRATION-ONLY mode — the live encoder patch is disabled until "
                + "the flattened-serializer offsets are verified on this build. Hooks register but values "
                + "are not yet substituted. See README.");

        return true;
    }

    /// <summary>
    ///     Read-only resolution self-test: locate the encode-path functions on the live binary via
    ///     their referenced log strings (<see cref="Sharp.Shared.ILibraryModule.FindFunction(string)"/>).
    ///     Logs the resolved addresses. Touches no memory — this is the Phase-1 offset-verification
    ///     groundwork (confirms the string-anchor resolution works on this build before any patch).
    /// </summary>
    // Function entry (file-vaddr 0x3334e0) that owns the "EncodeField encoder wrote %d bits" string.
    // Canonical shortest-unique sig from nosoop's makesig (21 bytes, no wildcards — the prologue has
    // no ADDRESS/DYNAMIC operands). Earlier 0x4334d0 (realloc helper) and 0x3356dd (mid-instruction)
    // were WRONG. DYNAMICALLY CONFIRMED (gdb 2026-06-14): fires per field-encode, rsi=0 (shared encode).
    private const string EncodeFieldSig =
        "55 48 89 E5 41 57 49 89 D7 41 56 41 55 41 54 41 BC 01 00 00 00";

    private void ResolveNativeTargets()
    {
        var ns = _bridge.LibraryModuleManager.NetworkSystem;
        var en = _bridge.LibraryModuleManager.Engine;

        // EncodeField: resolve by byte-signature (FindString fails on its long format strings).
        try
        {
            var ef = ns.FindPattern(EncodeFieldSig);
            _encodeFieldAddr = ef;
            _logger.LogInformation("SendProxy resolve EncodeField (sig): fn=0x{Fn:X}", ef);
            if (ef == 0)
                _logger.LogWarning("SendProxy: EncodeField sig not found (changed this build?)");
        }
        catch (Exception e)
        {
            _logger.LogWarning("SendProxy EncodeField sig resolve failed: {Msg}", e.Message);
        }

        ResolveByString(en, "SendClientMessages", "SV:  SendClientMessages");
        ResolveByString(en, "WriteDeltaEntity",
            "SV: CNetworkGameServerBase::WriteDeltaEntity_Internal merging changes added in %d additional fields!");
    }

    // Locate a function by a string it references: FindString -> FindFunction(ptr). Both can throw
    // when there's no match, so each lookup is isolated. Read-only.
    private void ResolveByString(Sharp.Shared.ILibraryModule lib, string name, string anchor)
    {
        try
        {
            var strAddr = lib.FindString(anchor);
            if (strAddr == 0)
            {
                _logger.LogWarning("SendProxy resolve {Name}: string anchor not found (changed this build?)", name);
                return;
            }

            var fn = lib.FindFunction(strAddr);
            _logger.LogInformation("SendProxy resolve {Name}: string=0x{Str:X} fn=0x{Fn:X}", name, strAddr, fn);
        }
        catch (Exception e)
        {
            _logger.LogWarning("SendProxy resolve {Name} failed: {Msg}", name, e.Message);
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
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ConVarManager.ReleaseCommand("sp_dump");
        _bridge.ConVarManager.ReleaseCommand("sp_detour_on");
        _bridge.ConVarManager.ReleaseCommand("sp_detour_off");
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

    private ECommandAction OnDetourOn(StringCommand command)
    {
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
}
