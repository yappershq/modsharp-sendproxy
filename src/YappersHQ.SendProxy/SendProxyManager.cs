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
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

internal sealed class SendProxyManager : ISendProxyManager
{
    private const int GameRulesEntity = -1;

    private readonly ILogger                                                       _logger;
    private readonly Dictionary<(int Entity, string Prop), List<HookEntry>>        _hooks       = new();
    private readonly Dictionary<(int Entity, string Prop), List<PropChangeCallback>> _changeHooks = new();

    // Set by SendProxyModule.PostInit; installs the substitution sub-detours on first per-client use.
    private Func<bool>? _ensureSubDetours;

    public SendProxyManager(ILogger logger)
        => _logger = logger;

    internal void SetSubDetourInstaller(Func<bool> installer)
        => _ensureSubDetours = installer;

    private sealed record HookEntry(SendPropType Type, Delegate Callback, int Element);

    #region Entity-scoped hook bookkeeping

    private bool AddHook(int entity, string prop, SendPropType type, Delegate cb, int element)
    {
        if (string.IsNullOrEmpty(prop) || cb is null)
        {
            return false;
        }

        var key = (entity, prop);
        if (!_hooks.TryGetValue(key, out var list))
        {
            _hooks[key] = list = [];
        }

        list.Add(new HookEntry(type, cb, element));

        return true;
    }

    public bool HookInt(int entity, string prop, SendProxyIntCallback callback)
        => AddHook(entity, prop, SendPropType.Int, callback, 0);

    public bool HookFloat(int entity, string prop, SendProxyFloatCallback callback)
        => AddHook(entity, prop, SendPropType.Float, callback, 0);

    public bool HookString(int entity, string prop, SendProxyStringCallback callback)
        => AddHook(entity, prop, SendPropType.String, callback, 0);

    public bool HookVector(int entity, string prop, SendProxyVectorCallback callback)
        => AddHook(entity, prop, SendPropType.Vector, callback, 0);

    public bool HookArrayInt(int entity, string prop, int element, SendProxyIntCallback callback)
        => AddHook(entity, prop, SendPropType.Int, callback, element);

    public bool HookArrayFloat(int entity, string prop, int element, SendProxyFloatCallback callback)
        => AddHook(entity, prop, SendPropType.Float, callback, element);

    public bool HookGameRulesInt(string prop, SendProxyIntCallback callback)
        => AddHook(GameRulesEntity, prop, SendPropType.Int, callback, 0);

    public bool HookGameRulesFloat(string prop, SendProxyFloatCallback callback)
        => AddHook(GameRulesEntity, prop, SendPropType.Float, callback, 0);

    public bool Unhook(int entity, string prop, Delegate callback)
    {
        if (!_hooks.TryGetValue((entity, prop), out var list))
        {
            return false;
        }

        var removed = list.RemoveAll(h => h.Callback == callback) > 0;
        if (list.Count == 0)
        {
            _hooks.Remove((entity, prop));
        }

        return removed;
    }

    public bool Unhook(int entity, string prop)
        => _hooks.Remove((entity, prop));

    public bool UnhookGameRules(string prop, Delegate callback)
        => Unhook(GameRulesEntity, prop, callback);

    /// <summary>
    ///     Drop every hook bound to <paramref name="entity"/>. Called from OnEntityDeleted — entity
    ///     indices are reused after disconnect/round restart so stale hooks must not persist.
    /// </summary>
    internal void RemoveEntityHooks(int entity)
    {
        foreach (var key in _hooks.Keys.Where(k => k.Entity == entity).ToList())
        {
            _hooks.Remove(key);
        }

        foreach (var key in _changeHooks.Keys.Where(k => k.Entity == entity).ToList())
        {
            _changeHooks.Remove(key);
        }
    }

    public bool IsHooked(int entity, string prop)
        => _hooks.ContainsKey((entity, prop));

    public bool IsHookedGameRules(string prop)
        => _hooks.ContainsKey((GameRulesEntity, prop));

    public bool HookPropChange(int entity, string prop, PropChangeCallback callback)
    {
        var key = (entity, prop);
        if (!_changeHooks.TryGetValue(key, out var list))
        {
            _changeHooks[key] = list = [];
        }

        list.Add(callback);

        return true;
    }

    public bool UnhookPropChange(int entity, string prop, PropChangeCallback callback)
        => _changeHooks.TryGetValue((entity, prop), out var list) && list.Remove(callback);

    #endregion

    #region Per-client field substitution

    private bool EnsureDetours(string context)
    {
        if (_ensureSubDetours is { } installer)
        {
            if (!installer())
            {
                _logger.LogWarning("SendProxy: {Ctx} — substitution sub-detours failed", context);

                return false;
            }

            return true;
        }

        _logger.LogWarning("SendProxy: {Ctx} — substitution installer not wired yet", context);

        return false;
    }

    /// <inheritdoc/>
    public void HookInt(string serializerName, string fieldName, PerClientIntProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookInt(\"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client int callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityInt(int entityIndex, string serializerName, string fieldName, PerClientIntProxy callback)
    {
        if (entityIndex < 0)
        {
            _logger.LogWarning("SendProxy: HookEntityInt — entityIndex must be >= 0 (got {Idx}); use HookInt for all-entity scope", entityIndex);

            return;
        }

        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookEntityInt(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client int callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void SetEntitySpoof(int entityIndex, string serializerName, string fieldName, int value)
    {
        if (entityIndex < 0)
        {
            _logger.LogWarning("SendProxy: SetEntitySpoof — entityIndex must be >= 0 (got {Idx})", entityIndex);

            return;
        }

        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
        {
            return;
        }

        if (!EnsureDetours($"SetEntitySpoof(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetEntitySpoof(entityIndex, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: entity spoof registered ent={Ent} \"{Ser}::{Field}\" → {Value}",
            entityIndex, serializerName, fieldName, value);
    }

    /// <inheritdoc/>
    public void UnhookEntity(int entityIndex, string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
        {
            return;
        }

        FieldSubstitution.ClearEntityRegistration(entityIndex, serializerName, fieldName);
        _logger.LogInformation(
            "SendProxy: entity-specific registration removed for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void UnhookInt(string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
        {
            return;
        }

        FieldSubstitution.ClearCallback(serializerName, fieldName);
        _logger.LogInformation(
            "SendProxy: global per-client callback removed for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void Unhook(string serializerName, string fieldName)
        => UnhookInt(serializerName, fieldName);

    /// <inheritdoc/>
    public void HookFloat(string serializerName, string fieldName, PerClientFloatProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookFloat(\"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client float callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityFloat(int entityIndex, string serializerName, string fieldName, PerClientFloatProxy callback)
    {
        if (entityIndex < 0)
        {
            _logger.LogWarning("SendProxy: HookEntityFloat — entityIndex must be >= 0 (got {Idx})", entityIndex);

            return;
        }

        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookEntityFloat(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client float callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookBool(string serializerName, string fieldName, PerClientBoolProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookBool(\"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client bool callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityBool(int entityIndex, string serializerName, string fieldName, PerClientBoolProxy callback)
    {
        if (entityIndex < 0)
        {
            _logger.LogWarning("SendProxy: HookEntityBool — entityIndex must be >= 0 (got {Idx})", entityIndex);

            return;
        }

        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookEntityBool(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client bool callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookVector(string serializerName, string fieldName, PerClientVectorProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookVector(\"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client vector/qangle callback (all entities) registered for \"{Ser}::{Field}\"",
            serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void HookEntityVector(int entityIndex, string serializerName, string fieldName, PerClientVectorProxy callback)
    {
        if (entityIndex < 0)
        {
            _logger.LogWarning("SendProxy: HookEntityVector — entityIndex must be >= 0 (got {Idx})", entityIndex);

            return;
        }

        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null)
        {
            return;
        }

        if (!EnsureDetours($"HookEntityVector(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetEntityCallback(entityIndex, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: per-client vector/qangle callback registered for ent={Ent} \"{Ser}::{Field}\"",
            entityIndex, serializerName, fieldName);
    }

    /// <inheritdoc/>
    public void UnhookAllPerClient()
    {
        FieldSubstitution.ClearAll();
        FieldSubstitution.Uninstall();
        _logger.LogInformation("SendProxy: all per-client registrations cleared, substitution detours uninstalled");
    }

    /// <inheritdoc/>
    public void SetUniformInt(string serializerName, string fieldName, int value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
        {
            return;
        }

        if (!EnsureDetours($"SetUniformInt(\"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetSpoof(serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: uniform int spoof registered (all entities) \"{Ser}::{Field}\" → {Value}",
            serializerName, fieldName, value);
    }

    /// <inheritdoc/>
    public void SetUniformIntForEntity(int entityIndex, string serializerName, string fieldName, int value)
    {
        if (entityIndex < 0)
        {
            _logger.LogWarning("SendProxy: SetUniformIntForEntity — entityIndex must be >= 0 (got {Idx})", entityIndex);

            return;
        }

        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
        {
            return;
        }

        if (!EnsureDetours($"SetUniformIntForEntity(ent={entityIndex}, \"{serializerName}::{fieldName}\")"))
        {
            return;
        }

        FieldSubstitution.SetEntitySpoof(entityIndex, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;

        _logger.LogInformation(
            "SendProxy: uniform int spoof registered ent={Ent} \"{Ser}::{Field}\" → {Value}",
            entityIndex, serializerName, fieldName, value);
    }

    #endregion

    internal void Clear()
    {
        _hooks.Clear();
        _changeHooks.Clear();
    }
}
