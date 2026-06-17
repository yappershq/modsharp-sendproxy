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
using Sharp.Shared.Objects;
using YappersHQ.SendProxy.Native;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy;

internal sealed class SendProxyManager : ISendProxyManager
{
    private readonly ILogger _logger;

    // Set by SendProxyModule.PostInit; installs the substitution sub-detours on first use.
    private Func<bool>? _ensureSubDetours;

    // Installs the uniform encoder detours (all-clients substitution) on first use.
    private Func<bool>? _ensureUniformHook;

    public SendProxyManager(ILogger logger)
        => _logger = logger;

    internal void SetSubDetourInstaller(Func<bool> installer)
        => _ensureSubDetours = installer;

    internal void SetUniformHookInstaller(Func<bool> installer)
        => _ensureUniformHook = installer;

    private bool EnsureUniformHook(string context)
    {
        if (_ensureUniformHook is { } installer && installer())
        {
            return true;
        }

        _logger.LogWarning("SendProxy: {Ctx} — uniform encoder hook unavailable", context);

        return false;
    }

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

    // Uniform (all-clients) substitution rides the per-field ENCODER hook (UniformEncoderHook): the
    // encoder fires once during the shared pack and receives the field's fieldInfo directly, so the
    // field is matched unambiguously and every client sees the value. (The bit-copy path can't reliably
    // pair a value-copy to its field, so it's reserved for per-client callbacks.) Matched by field name.

    public void SetUniform(string serializerName, string fieldName, int value)
    {
        if (string.IsNullOrEmpty(fieldName) || !EnsureUniformHook($"SetUniform(\"{fieldName}\", int)")) return;
        UniformEncoderHook.SetInt(fieldName, value);
        _logger.LogInformation("SendProxy: uniform int spoof \"{Field}\" → {Value}", fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, float value)
    {
        if (string.IsNullOrEmpty(fieldName) || !EnsureUniformHook($"SetUniform(\"{fieldName}\", float)")) return;
        UniformEncoderHook.SetFloat(fieldName, value);
        _logger.LogInformation("SendProxy: uniform float spoof \"{Field}\" → {Value}", fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, bool value)
    {
        if (string.IsNullOrEmpty(fieldName) || !EnsureUniformHook($"SetUniform(\"{fieldName}\", bool)")) return;
        UniformEncoderHook.SetBool(fieldName, value);
        _logger.LogInformation("SendProxy: uniform bool spoof \"{Field}\" → {Value}", fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, Vector3 value)
    {
        if (string.IsNullOrEmpty(fieldName) || !EnsureUniformHook($"SetUniform(\"{fieldName}\", vector)")) return;
        UniformEncoderHook.SetVector(fieldName, value);
        _logger.LogInformation("SendProxy: uniform vector spoof \"{Field}\" → {Value}", fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, string value)
    {
        if (string.IsNullOrEmpty(fieldName) || !EnsureUniformHook($"SetUniform(\"{fieldName}\", string)")) return;
        UniformEncoderHook.SetString(fieldName, value);
        _logger.LogInformation("SendProxy: uniform string spoof \"{Field}\" → \"{Value}\"", fieldName, value);
    }

    public void SetUniform(string serializerName, string fieldName, byte[] value)
    {
        if (string.IsNullOrEmpty(fieldName) || !EnsureUniformHook($"SetUniform(\"{fieldName}\", bytes)")) return;
        UniformEncoderHook.SetBytes(fieldName, value);
        _logger.LogInformation("SendProxy: uniform bytes spoof \"{Field}\" ({Len} bytes)", fieldName, value.Length);
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

    // -- SendFake (one-shot push to a single client) ----------------------------------------------

    // Register a one-shot entity-scoped substitution bound to this client, then force the field dirty so
    // the engine re-transmits it on the next snapshot (NetworkStateChanged sets the per-field dirty bit,
    // which drives the entity into the change-list regardless of whether the real value changed). The
    // substitution fires once for that client and self-removes; other clients pass through untouched.
    private bool BeginSendFake(IGameClient? client, IBaseEntity? entity, string serializerName, string fieldName, out int idx, out nint clientPtr)
    {
        idx       = -1;
        clientPtr = 0;
        if (client is null || entity is null || string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
        {
            return false;
        }

        if (!EnsureDetours($"SendFake(\"{serializerName}::{fieldName}\")"))
        {
            return false;
        }

        idx       = (int) entity.Index;
        clientPtr = client.GetAbsPtr();

        return clientPtr != 0;
    }

    public void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, int value)
    {
        if (!BeginSendFake(client, entity, serializerName, fieldName, out var idx, out var clientPtr)) return;
        FieldSubstitution.SetOneShot(idx, serializerName, fieldName, clientPtr, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        entity.NetworkStateChanged(fieldName);
        _logger.LogInformation("SendProxy: one-shot int fake ent={Ent} client=0x{C:X} \"{Ser}::{Field}\" → {Value}", idx, clientPtr, serializerName, fieldName, value);
    }

    public void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, float value)
    {
        if (!BeginSendFake(client, entity, serializerName, fieldName, out var idx, out var clientPtr)) return;
        FieldSubstitution.SetOneShot(idx, serializerName, fieldName, clientPtr, BitConverter.SingleToInt32Bits(value));
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        entity.NetworkStateChanged(fieldName);
        _logger.LogInformation("SendProxy: one-shot float fake ent={Ent} client=0x{C:X} \"{Ser}::{Field}\" → {Value}", idx, clientPtr, serializerName, fieldName, value);
    }

    public void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, bool value)
    {
        if (!BeginSendFake(client, entity, serializerName, fieldName, out var idx, out var clientPtr)) return;
        FieldSubstitution.SetOneShot(idx, serializerName, fieldName, clientPtr, value ? 1 : 0);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        entity.NetworkStateChanged(fieldName);
        _logger.LogInformation("SendProxy: one-shot bool fake ent={Ent} client=0x{C:X} \"{Ser}::{Field}\" → {Value}", idx, clientPtr, serializerName, fieldName, value);
    }

    public void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, Vector3 value)
    {
        if (!BeginSendFake(client, entity, serializerName, fieldName, out var idx, out var clientPtr)) return;
        FieldSubstitution.SetOneShot(idx, serializerName, fieldName, clientPtr, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        entity.NetworkStateChanged(fieldName);
        _logger.LogInformation("SendProxy: one-shot vector fake ent={Ent} client=0x{C:X} \"{Ser}::{Field}\" → {Value}", idx, clientPtr, serializerName, fieldName, value);
    }

    public void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, string value)
    {
        if (!BeginSendFake(client, entity, serializerName, fieldName, out var idx, out var clientPtr)) return;
        FieldSubstitution.SetOneShot(idx, serializerName, fieldName, clientPtr, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        entity.NetworkStateChanged(fieldName);
        _logger.LogInformation("SendProxy: one-shot string fake ent={Ent} client=0x{C:X} \"{Ser}::{Field}\" → \"{Value}\"", idx, clientPtr, serializerName, fieldName, value);
    }

    public void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, byte[] value)
    {
        if (!BeginSendFake(client, entity, serializerName, fieldName, out var idx, out var clientPtr)) return;
        FieldSubstitution.SetOneShot(idx, serializerName, fieldName, clientPtr, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        entity.NetworkStateChanged(fieldName);
        _logger.LogInformation("SendProxy: one-shot bytes fake ent={Ent} client=0x{C:X} \"{Ser}::{Field}\" ({Len} bytes)", idx, clientPtr, serializerName, fieldName, value.Length);
    }

    // -- Removal ----------------------------------------------------------------------------------

    public void Unhook(string serializerName, string fieldName)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        UniformEncoderHook.Remove(fieldName);                  // uniform (encoder hook)
        FieldSubstitution.ClearGlobal(serializerName, fieldName); // per-client (bit-copy path)
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
        UniformEncoderHook.Uninstall();
        FieldSubstitution.ClearAll();
        FieldSubstitution.Uninstall();
        _logger.LogInformation("SendProxy: all registrations cleared, detours uninstalled");
    }

    public bool IsHooked(string serializerName, string fieldName)
        => UniformEncoderHook.HasAny || FieldSubstitution.IsHooked(serializerName, fieldName);

    public bool SetForceResend(bool enabled)
    {
        var ok = ForceResend.SetEnabled(enabled);
        _logger.LogInformation("SendProxy: force-resend {State}{Result}", enabled ? "enable" : "disable",
            ok ? "" : " (FAILED to install)");

        return ok;
    }
}
