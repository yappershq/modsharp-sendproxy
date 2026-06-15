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
using System.Numerics;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

internal sealed class SendProxyManager : ISendProxyManager
{
    private readonly ILogger _logger;

    // Set by SendProxyModule.PostInit; installs the substitution sub-detours on first use.
    private Func<bool>? _ensureSubDetours;

    public SendProxyManager(ILogger logger)
        => _logger = logger;

    internal void SetSubDetourInstaller(Func<bool> installer)
        => _ensureSubDetours = installer;

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

    internal void Clear() { /* registry lives in FieldSubstitution; nothing to clear here */ }

    // Drop every registration scoped to entityIndex (called from OnEntityDeleted — indices are reused).
    internal void RemoveEntityRegistrations(int entityIndex)
        => FieldSubstitution.ClearEntityIndex(entityIndex);

    // Drop every callback owned by an unloading consumer module (called from OnLibraryDisconnect) — a
    // dangling delegate into an unloaded module would crash the send path.
    internal void RemoveOwnerRegistrations(string moduleName)
    {
        var removed = FieldSubstitution.PurgeOwner(moduleName);
        if (removed > 0)
        {
            _logger.LogInformation("SendProxy: purged {Count} callback(s) owned by unloaded module \"{Module}\"", removed, moduleName);
        }
    }

    // -- Hook, all entities -----------------------------------------------------------------------

    public void Hook(string serializerName, string fieldName, PerClientIntProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureDetours($"Hook(\"{serializerName}::{fieldName}\", int)")) return;
        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client int callback (all entities) registered for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Hook(string serializerName, string fieldName, PerClientFloatProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureDetours($"Hook(\"{serializerName}::{fieldName}\", float)")) return;
        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client float callback (all entities) registered for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Hook(string serializerName, string fieldName, PerClientBoolProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureDetours($"Hook(\"{serializerName}::{fieldName}\", bool)")) return;
        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client bool callback (all entities) registered for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Hook(string serializerName, string fieldName, PerClientVectorProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureDetours($"Hook(\"{serializerName}::{fieldName}\", vector)")) return;
        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client vector callback (all entities) registered for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Hook(string serializerName, string fieldName, PerClientStringProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureDetours($"Hook(\"{serializerName}::{fieldName}\", string)")) return;
        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client string callback (all entities) registered for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Hook(string serializerName, string fieldName, PerClientBytesProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureDetours($"Hook(\"{serializerName}::{fieldName}\", bytes)")) return;
        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client bytes callback (all entities) registered for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    // -- Hook, single entity ----------------------------------------------------------------------

    public void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientIntProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"Hook(ent={idx}, \"{serializerName}::{fieldName}\", int)")) return;
        FieldSubstitution.SetEntityCallback(idx, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client int callback registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientFloatProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"Hook(ent={idx}, \"{serializerName}::{fieldName}\", float)")) return;
        FieldSubstitution.SetEntityCallback(idx, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client float callback registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientBoolProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"Hook(ent={idx}, \"{serializerName}::{fieldName}\", bool)")) return;
        FieldSubstitution.SetEntityCallback(idx, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client bool callback registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientVectorProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"Hook(ent={idx}, \"{serializerName}::{fieldName}\", vector)")) return;
        FieldSubstitution.SetEntityCallback(idx, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client vector callback registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientStringProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"Hook(ent={idx}, \"{serializerName}::{fieldName}\", string)")) return;
        FieldSubstitution.SetEntityCallback(idx, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client string callback registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientBytesProxy callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"Hook(ent={idx}, \"{serializerName}::{fieldName}\", bytes)")) return;
        FieldSubstitution.SetEntityCallback(idx, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client bytes callback registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    // -- SetUniform, all entities -----------------------------------------------------------------

    public void SetUniform(string serializerName, string fieldName, int value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (!EnsureDetours($"SetUniform(\"{serializerName}::{fieldName}\", int)")) return;
        FieldSubstitution.SetSpoof(serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform int spoof (all entities) \"{Ser}::{Field}\" → {Value}", serializerName, fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, float value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (!EnsureDetours($"SetUniform(\"{serializerName}::{fieldName}\", float)")) return;
        FieldSubstitution.SetSpoof(serializerName, fieldName, BitConverter.SingleToInt32Bits(value));
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform float spoof (all entities) \"{Ser}::{Field}\" → {Value}", serializerName, fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, bool value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (!EnsureDetours($"SetUniform(\"{serializerName}::{fieldName}\", bool)")) return;
        FieldSubstitution.SetSpoof(serializerName, fieldName, value ? 1 : 0);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform bool spoof (all entities) \"{Ser}::{Field}\" → {Value}", serializerName, fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, Vector3 value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (!EnsureDetours($"SetUniform(\"{serializerName}::{fieldName}\", vector)")) return;
        FieldSubstitution.SetSpoof(serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform vector spoof (all entities) \"{Ser}::{Field}\" → {Value}", serializerName, fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, string value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (!EnsureDetours($"SetUniform(\"{serializerName}::{fieldName}\", string)")) return;
        FieldSubstitution.SetSpoof(serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform string spoof (all entities) \"{Ser}::{Field}\" → \"{Value}\"", serializerName, fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, byte[] value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (!EnsureDetours($"SetUniform(\"{serializerName}::{fieldName}\", bytes)")) return;
        FieldSubstitution.SetSpoof(serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform bytes spoof (all entities) \"{Ser}::{Field}\" ({Len} bytes)", serializerName, fieldName, value.Length);
    }

    // -- SetUniform, single entity ----------------------------------------------------------------

    public void SetUniform(IBaseEntity entity, string serializerName, string fieldName, int value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"SetUniform(ent={idx}, \"{serializerName}::{fieldName}\", int)")) return;
        FieldSubstitution.SetEntitySpoof(idx, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform int spoof ent={Ent} \"{Ser}::{Field}\" → {Value}", idx, serializerName, fieldName, value);
    }

    public void SetUniform(IBaseEntity entity, string serializerName, string fieldName, float value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"SetUniform(ent={idx}, \"{serializerName}::{fieldName}\", float)")) return;
        FieldSubstitution.SetEntitySpoof(idx, serializerName, fieldName, BitConverter.SingleToInt32Bits(value));
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform float spoof ent={Ent} \"{Ser}::{Field}\" → {Value}", idx, serializerName, fieldName, value);
    }

    public void SetUniform(IBaseEntity entity, string serializerName, string fieldName, bool value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"SetUniform(ent={idx}, \"{serializerName}::{fieldName}\", bool)")) return;
        FieldSubstitution.SetEntitySpoof(idx, serializerName, fieldName, value ? 1 : 0);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform bool spoof ent={Ent} \"{Ser}::{Field}\" → {Value}", idx, serializerName, fieldName, value);
    }

    public void SetUniform(IBaseEntity entity, string serializerName, string fieldName, Vector3 value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"SetUniform(ent={idx}, \"{serializerName}::{fieldName}\", vector)")) return;
        FieldSubstitution.SetEntitySpoof(idx, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform vector spoof ent={Ent} \"{Ser}::{Field}\" → {Value}", idx, serializerName, fieldName, value);
    }

    public void SetUniform(IBaseEntity entity, string serializerName, string fieldName, string value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"SetUniform(ent={idx}, \"{serializerName}::{fieldName}\", string)")) return;
        FieldSubstitution.SetEntitySpoof(idx, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform string spoof ent={Ent} \"{Ser}::{Field}\" → \"{Value}\"", idx, serializerName, fieldName, value);
    }

    public void SetUniform(IBaseEntity entity, string serializerName, string fieldName, byte[] value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        var idx = (int) entity.Index;
        if (!EnsureDetours($"SetUniform(ent={idx}, \"{serializerName}::{fieldName}\", bytes)")) return;
        FieldSubstitution.SetEntitySpoof(idx, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform bytes spoof ent={Ent} \"{Ser}::{Field}\" ({Len} bytes)", idx, serializerName, fieldName, value.Length);
    }

    // -- Removal ----------------------------------------------------------------------------------

    public void Unhook(string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        FieldSubstitution.ClearGlobal(serializerName, fieldName);
        _logger.LogInformation("SendProxy: global registration removed for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    public void Unhook(IBaseEntity entity, string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        var idx = (int) entity.Index;
        FieldSubstitution.ClearEntity(idx, serializerName, fieldName);
        _logger.LogInformation("SendProxy: entity registration removed for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    public void UnhookAll()
    {
        FieldSubstitution.ClearAll();
        FieldSubstitution.Uninstall();
        _logger.LogInformation("SendProxy: all registrations cleared, substitution detours uninstalled");
    }

    public bool IsHooked(string serializerName, string fieldName)
        => FieldSubstitution.IsHooked(serializerName, fieldName);
}
