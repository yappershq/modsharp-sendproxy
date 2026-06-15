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
    private const string SendClientMessagesKey = "CNetworkGameServer::SendClientMessages";
    private const string WriteDeltaInternalKey = "CNetworkGameServerBase::WriteDeltaEntity_Internal";
    private const string PerClientEncodeKey    = "CNetworkGameServer::PerClientEncode";
    private const string WriteFieldListKey     = "CFlattenedSerializer::WriteFieldList";
    private const string GetBitRangeKey        = "CFlattenedSerializer::GetBitRange";
    private const string BitCopyKey            = "CFlattenedSerializer::BitCopyPrimitive";
    private const string EncoderRegistryKey    = "CFlattenedSerializer::EncoderRegistry";
    private const string EncodeInt32Key        = "CFlattenedSerializer::EncodeInt32";

    private static readonly string[] EncoderBucketKeys =
    {
        "CFlattenedSerializer::EncoderBucket1",
        "CFlattenedSerializer::EncoderBucket2",
        "CFlattenedSerializer::EncoderBucket3",
        "CFlattenedSerializer::EncoderBucket4",
        "CFlattenedSerializer::EncoderBucket7",
    };

    private readonly ILogger<SendProxyModule> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly SendProxyManager         _manager;

    private nint _wdeAddr;
    private nint _perClientEncodeAddr;
    private nint _writeFieldListAddr;
    private nint _getBitRangeAddr;
    private nint _bitCopyAddr;
    private nint _registryAddr;
    private nint _encodeInt32Addr;

    public SendProxyModule(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        _logger  = sharedSystem.GetLoggerFactory().CreateLogger<SendProxyModule>();
        _bridge  = new InterfaceBridge(sharedSystem);
        _manager = new SendProxyManager(_logger);
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
        _manager.SetSubDetourInstaller(EnsureSubDetours);

        _bridge.SharpModuleManager.RegisterSharpModuleInterface<ISendProxyManager>(
            this, ISendProxyManager.Identity, _manager);

        _bridge.EntityManager.InstallEntityListener(this);
    }

    public void Shutdown()
    {
        RecipientCapture.Uninstall();
        FieldSubstitution.Uninstall();
        _bridge.EntityManager.RemoveEntityListener(this);
        _manager.Clear();
    }

    #endregion

    #region IEntityListener

    int IEntityListener.ListenerVersion  => IEntityListener.ApiVersion;
    int IEntityListener.ListenerPriority => 0;

    void IEntityListener.OnEntityDeleted(IBaseEntity entity)
        => _manager.RemoveEntityHooks((int) entity.Index);

    #endregion

    #region Native resolution

    private void ResolveNativeTargets()
    {
        ResolveFromGameData(SendClientMessagesKey);
        _wdeAddr             = ResolveFromGameData(WriteDeltaInternalKey);
        _perClientEncodeAddr = ResolveFromGameData(PerClientEncodeKey);
        _writeFieldListAddr  = ResolveFromGameData(WriteFieldListKey);
        _getBitRangeAddr     = ResolveFromGameData(GetBitRangeKey);
        _bitCopyAddr         = ResolveFromGameData(BitCopyKey);
        _registryAddr        = ResolveFromGameData(EncoderRegistryKey);
        _encodeInt32Addr     = ResolveFromGameData(EncodeInt32Key);

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

    private bool EnsureSubDetours()
    {
        if (_getBitRangeAddr == 0 || _bitCopyAddr == 0 || _writeFieldListAddr == 0)
        {
            _logger.LogWarning(
                "SendProxy: one or more substitution addresses not resolved "
                + "(GetBitRange=0x{Gbr:X} BitCopy=0x{Bc:X} WriteFieldList=0x{Wfl:X}) — "
                + "check gamedata sigs match this build",
                _getBitRangeAddr, _bitCopyAddr, _writeFieldListAddr);

            return false;
        }

        FieldSubstitution.GetBitRangeAddr    = _getBitRangeAddr;
        FieldSubstitution.ValueCopyAddr      = _bitCopyAddr;
        FieldSubstitution.WriteFieldListAddr = _writeFieldListAddr;
        FieldSubstitution.WdeAddr            = _wdeAddr;

        if (_perClientEncodeAddr != 0)
        {
            RecipientCapture.Install(_bridge, _logger, _perClientEncodeAddr);
        }

        return FieldSubstitution.Install(_bridge, _logger);
    }

    #endregion
}
