/*
 * SendProxy for ModSharp (CS2)
 * Copyright (C) 2026 YappersHQ. All Rights Reserved.
 *
 * This file is part of SendProxy for ModSharp.
 * SendProxy is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as
 * published by the Free Software Foundation, either version 3 of the
 * License, or (at your option) any later version.
 *
 * SendProxy is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with SendProxy. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

public sealed class SendProxyModule : IModSharpModule, IEntityListener
{
    private const string WriteDeltaInternalKey          = "CNetworkGameServerBase::WriteDeltaEntity_Internal";
    private const string PerClientEncodeKey             = "CNetworkGameServer::PerClientEncode";
    private const string WriteFieldListKey              = "CFlattenedSerializer::WriteFieldList";
    private const string GetBitRangeKey                 = "CFlattenedSerializer::GetBitRange";
    private const string WriteFieldListFieldPathSiteKey = "CFlattenedSerializer::WriteFieldList_FieldPathSite";
    private const string BitCopyKey                     = "CFlattenedSerializer::BitCopyPrimitive";
    private const string EncoderRegistryKey             = "CFlattenedSerializer::EncoderRegistry";
    private const string EncodeInt32Key                 = "CFlattenedSerializer::EncodeInt32";
    private const string EncodeKey                       = "CFlattenedSerializer::Encode";

    private static readonly string[] EncoderBucketKeys =
    {
        "CFlattenedSerializer::EncoderBucket1",
        "CFlattenedSerializer::EncoderBucket2",
        "CFlattenedSerializer::EncoderBucket3",
        "CFlattenedSerializer::EncoderBucket4",
        "CFlattenedSerializer::EncoderBucket5",
        "CFlattenedSerializer::EncoderBucket6",
        "CFlattenedSerializer::EncoderBucket7",
    };

    private readonly ILogger<SendProxyModule> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly ProxyManager             _proxyManager;

    private nint _wdeAddr;
    private nint _perClientEncodeAddr;
    private nint _writeFieldListAddr;
    private nint _getBitRangeAddr;
    private nint _writeFieldListFieldPathSiteAddr;
    private nint _bitCopyAddr;
    private nint _registryAddr;
    private nint _encodeInt32Addr;
    private nint _encodeAddr;

    public SendProxyModule(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        _logger       = sharedSystem.GetLoggerFactory().CreateLogger<SendProxyModule>();
        _bridge       = new InterfaceBridge(sharedSystem);
        _proxyManager = new ProxyManager(_logger);
    }

    public string DisplayName   => "SendProxy";
    public string DisplayAuthor => "YappersHQ";

    #region IModSharpModule

    public bool Init()
    {
        try
        {
            _bridge.GameData.Register("yappershq.sendproxy");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "SendProxy gamedata register failed (continuing without it)");
        }

        ResolveNativeTargets();

        return true;
    }

    public void PostInit()
    {
        _proxyManager.SetHookInstaller(EnsureProxyHooks);

        _bridge.SharpModuleManager.RegisterSharpModuleInterface<IProxyManager>(
            this, IProxyManager.Identity, _proxyManager);

        // Build the encoder classification map eagerly at load (resolution was set in Init) so
        // classification is ready and diagnosable from the boot log.
        FieldSubstitution.PrebuildEncoderMap(_logger);

        // Install all detours now, at load, instead of lazily on first registration. A lazy install of
        // ~17 encoder detours mid-game stalls a whole frame (~120 ms) the first time a spoof is set;
        // doing it at startup keeps gameplay smooth. Both installers are idempotent, so the lazy
        // Ensure* paths (still wired above) become no-ops after this.
        EnsureUniformHook();
        EnsureSubDetours();
        EnsureProxyHooks();

        _bridge.EntityManager.InstallEntityListener(this);
    }

    // A consumer module unloaded — purge any per-client callbacks it owns before the send path can
    // invoke a delegate into its unloaded AssemblyLoadContext (which would crash the server).
    public void OnLibraryDisconnect(string name)
    {
        _proxyManager.RemoveOwnerRegistrations(name);
    }

    public void Shutdown()
    {
        UniformEncoderHook.Uninstall();
        EncodeCapture.Uninstall();
        ProxyRegistry.Clear();
        PerClientDispatch.Clear();
        RecipientCapture.Uninstall();
        FieldSubstitution.Uninstall();
        _bridge.EntityManager.RemoveEntityListener(this);
    }

    #endregion

    #region IEntityListener

    int IEntityListener.ListenerVersion  => IEntityListener.ApiVersion;
    int IEntityListener.ListenerPriority => 0;

    // Evict any entity-scoped registration left on an index BOTH when the entity is created and when it
    // is deleted (mirrors TransmitManager): indices are reused, and a delete+create of the same index in
    // one frame could otherwise leak a stale spoof onto the new entity for a frame.
    void IEntityListener.OnEntityCreated(IBaseEntity entity)
    {
        _proxyManager.RemoveEntityRegistrations((int) entity.Index);
        PerClientDispatch.ClearEntity((int) entity.Index);
    }

    void IEntityListener.OnEntityDeleted(IBaseEntity entity)
    {
        _proxyManager.RemoveEntityRegistrations((int) entity.Index);
        PerClientDispatch.ClearEntity((int) entity.Index);
    }

    #endregion

    #region Native resolution

    private void ResolveNativeTargets()
    {
        _wdeAddr                         = ResolveFromGameData(WriteDeltaInternalKey);
        _perClientEncodeAddr             = ResolveFromGameData(PerClientEncodeKey);
        _writeFieldListAddr              = ResolveFromGameData(WriteFieldListKey);
        _getBitRangeAddr                 = ResolveFromGameData(GetBitRangeKey);
        // Windows-only field-path capture site (GetBitRange is inlined on Windows). The gamedata entry has
        // no linux sig, so only resolve it on Windows — avoids a spurious "not resolved" warning on Linux.
        _writeFieldListFieldPathSiteAddr = OperatingSystem.IsWindows()
            ? ResolveFromGameData(WriteFieldListFieldPathSiteKey)
            : 0;
        _bitCopyAddr                     = ResolveFromGameData(BitCopyKey);
        _registryAddr                    = ResolveFromGameData(EncoderRegistryKey);
        _encodeInt32Addr                 = ResolveFromGameData(EncodeInt32Key);
        _encodeAddr                      = ResolveFromGameData(EncodeKey);
        EncodeCapture.Addr               = _encodeAddr;

        var bucketAddrs = new nint[EncoderBucketKeys.Length];
        for (var i = 0; i < EncoderBucketKeys.Length; i++)
        {
            bucketAddrs[i] = ResolveFromGameData(EncoderBucketKeys[i]);
        }

        FieldSubstitution.SetEncoderResolution(_registryAddr, bucketAddrs, _encodeInt32Addr);
    }

    private nint ResolveFromGameData(string key)
    {
        try
        {
            var addr = _bridge.GameData.GetAddress(key);
            _logger.LogInformation("SendProxy resolve {Key}: addr=0x{Addr:X}", key, addr);

            return addr;
        }
        catch (Exception e)
        {
            _logger.LogWarning("SendProxy: {Key} not resolved: {Msg}", key, e.Message);

            return 0;
        }
    }

    // Installs the uniform encoder detours (all-clients substitution) lazily on first SetUniform. Uses
    // the encoder map FieldSubstitution prebuilds at load.
    private bool EnsureUniformHook()
        => UniformEncoderHook.Install(_bridge, _logger, FieldSubstitution.EncoderTypeMap);

    // Proxy dispatch (IProxyManager) rides the same per-field encoder detours as the uniform path and needs
    // the per-entity encode capture for entity/serializer context. Idempotent.
    private bool EnsureProxyHooks()
    {
        var encoderOk = EnsureUniformHook();
        var captureOk = EncodeCapture.Install(_bridge, _logger);

        return encoderOk && captureOk;
    }

    private bool EnsureSubDetours()
    {
        // The field-path capture address differs by platform: Linux uses GetBitRange, Windows uses the
        // inlined per-field site. Check the correct one is resolved rather than always requiring both.
        var fieldPathOk = OperatingSystem.IsWindows()
            ? _writeFieldListFieldPathSiteAddr != 0
            : _getBitRangeAddr != 0;

        if (!fieldPathOk || _bitCopyAddr == 0 || _writeFieldListAddr == 0)
        {
            if (OperatingSystem.IsWindows())
            {
                _logger.LogWarning(
                    "SendProxy: one or more substitution addresses not resolved "
                    + "(WriteFieldList_FieldPathSite=0x{Site:X} BitCopy=0x{Bc:X} WriteFieldList=0x{Wfl:X}) — "
                    + "check gamedata sigs match this build",
                    _writeFieldListFieldPathSiteAddr, _bitCopyAddr, _writeFieldListAddr);
            }
            else
            {
                _logger.LogWarning(
                    "SendProxy: one or more substitution addresses not resolved "
                    + "(GetBitRange=0x{Gbr:X} BitCopy=0x{Bc:X} WriteFieldList=0x{Wfl:X}) — "
                    + "check gamedata sigs match this build",
                    _getBitRangeAddr, _bitCopyAddr, _writeFieldListAddr);
            }

            return false;
        }

        FieldSubstitution.GetBitRangeAddr            = _getBitRangeAddr;
        FieldSubstitution.WindowsFieldPathSiteAddr   = _writeFieldListFieldPathSiteAddr;
        FieldSubstitution.ValueCopyAddr              = _bitCopyAddr;
        FieldSubstitution.WriteFieldListAddr         = _writeFieldListAddr;
        FieldSubstitution.WdeAddr                    = _wdeAddr;

        if (_perClientEncodeAddr != 0)
        {
            RecipientCapture.Install(_bridge, _logger, _perClientEncodeAddr);
        }

        return FieldSubstitution.Install(_bridge, _logger);
    }

    #endregion
}
