using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

public sealed class SendProxyModule : IModSharpModule
{
    public string DisplayName   => "SendProxy";
    public string DisplayAuthor => "Prefix";

    private readonly ILogger<SendProxyModule> _logger;
    private readonly InterfaceBridge _bridge;
    private readonly SendProxyManager _manager;

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
    private void ResolveNativeTargets()
    {
        var ns = _bridge.LibraryModuleManager.NetworkSystem;
        var en = _bridge.LibraryModuleManager.Engine;

        ResolveByString(ns, "EncodeField", "CFlattenedSerializer::EncodeField encoder wrote %d bits %s %s %s!");
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
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<ISendProxyManager>(
            this, ISendProxyManager.Identity, _manager);

    public void Shutdown() => _manager.Clear();
}
