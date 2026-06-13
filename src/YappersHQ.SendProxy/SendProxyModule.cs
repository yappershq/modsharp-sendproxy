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
        // Loads .asset/gamedata/yappershq.sendproxy.games.jsonc (offsets/sigs for the encode path).
        _bridge.GameData.Register("yappershq.sendproxy");

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
        try
        {
            var ns = _bridge.LibraryModuleManager.NetworkSystem;
            var en = _bridge.LibraryModuleManager.Engine;

            var encodeField = ns.FindFunction("CFlattenedSerializer::EncodeField encoder wrote %d bits %s %s %s!");
            var sendClients = en.FindFunction("SV:  SendClientMessages");
            var writeDelta  = en.FindFunction(
                "SV: CNetworkGameServerBase::WriteDeltaEntity_Internal merging changes added in %d additional fields!");

            _logger.LogInformation(
                "SendProxy native resolution — EncodeField={EncodeField:X}, SendClientMessages={Send:X}, WriteDeltaEntity={Write:X}",
                encodeField, sendClients, writeDelta);

            if (encodeField == 0)
                _logger.LogWarning("EncodeField not resolved by string anchor — anchor may have changed this build");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "SendProxy native resolution self-test threw");
        }
    }

    public void PostInit()
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<ISendProxyManager>(
            this, ISendProxyManager.Identity, _manager);

    public void Shutdown() => _manager.Clear();
}
