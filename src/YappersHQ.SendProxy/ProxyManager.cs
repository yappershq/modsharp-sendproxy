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
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

internal sealed class ProxyManager : IProxyManager
{
    private readonly ILogger _logger;

    // Set by the module — installs the encode-capture + encoder hooks on first registration.
    private Func<bool>? _ensureHooks;

    public ProxyManager(ILogger logger)
        => _logger = logger;

    internal void SetHookInstaller(Func<bool> installer)
        => _ensureHooks = installer;

    private bool EnsureHooks(string context)
    {
        if (_ensureHooks is { } installer && installer())
        {
            return true;
        }

        _logger.LogWarning("SendProxy: {Ctx} — proxy hooks unavailable", context);

        return false;
    }

    // Drop registrations scoped to an entity index (OnEntityCreated/Deleted — indices are reused).
    internal void RemoveEntityRegistrations(int entityIndex)
        => ProxyRegistry.ClearEntity(entityIndex);

    // Drop callbacks owned by an unloading consumer (OnLibraryDisconnect) — a dangling delegate into an
    // unloaded AssemblyLoadContext would crash the encode thread.
    internal void RemoveOwnerRegistrations(string moduleName)
    {
        var removed = ProxyRegistry.PurgeOwner(moduleName);
        if (removed > 0)
        {
            _logger.LogInformation("SendProxy: purged {Count} proxy(ies) owned by unloaded module \"{Module}\"", removed, moduleName);
        }
    }

    #region IProxyManager

    public void Register(string serializerName, string fieldName, ProxyCallback callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureHooks($"Register(\"{serializerName}::{fieldName}\")")) return;

        ProxyRegistry.Set(serializerName, fieldName, -1, callback);
        _logger.LogInformation("SendProxy: proxy registered (all entities) for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Register(IBaseEntity entity, string serializerName, string fieldName, ProxyCallback callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (entity is not { IsValidEntity: true }) return;
        if (!EnsureHooks($"Register(ent={(int) entity.Index}, \"{serializerName}::{fieldName}\")")) return;

        var idx = (int) entity.Index;
        ProxyRegistry.Set(serializerName, fieldName, idx, callback);

        // Force an immediate re-pack so the proxy applies next tick instead of waiting for the entity to
        // naturally change — the value lands in the shared snapshot on that pack. Entity-level dirty only.
        try
        {
            entity.NetworkStateChanged(fieldName);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "SendProxy: NetworkStateChanged(\"{Field}\") on register failed (non-fatal)", fieldName);
        }

        _logger.LogInformation("SendProxy: proxy registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public void Unregister(string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        ProxyRegistry.Remove(serializerName, fieldName, -1);
        _logger.LogInformation("SendProxy: proxy removed (all entities) for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Unregister(IBaseEntity entity, string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (entity is not { IsValidEntity: true }) return;

        var idx = (int) entity.Index;
        ProxyRegistry.Remove(serializerName, fieldName, idx);

        // Re-dirty so the real value is re-sent now that the proxy is gone.
        try { entity.NetworkStateChanged(fieldName); } catch { /* non-fatal */ }

        _logger.LogInformation("SendProxy: proxy removed for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public bool IsRegistered(string serializerName, string fieldName)
        => !string.IsNullOrEmpty(serializerName) && !string.IsNullOrEmpty(fieldName)
            && ProxyRegistry.Has(serializerName, fieldName);

    #endregion
}
