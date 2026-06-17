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

    public void Hook(string serializerName, string fieldName, SendProxyCallback callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (!EnsureDetours($"Hook(\"{serializerName}::{fieldName}\")")) return;
        FieldSubstitution.SetCallback(serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client callback (all entities) registered for \"{Ser}::{Field}\"", serializerName, fieldName);
    }

    // -- Hook, single entity ----------------------------------------------------------------------

    public void Hook(IBaseEntity entity, string serializerName, string fieldName, SendProxyCallback callback)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName) || callback is null) return;
        if (entity is not { IsValidEntity: true })
        {
            return;
        }

        var idx = (int) entity.Index;
        if (!EnsureDetours($"Hook(ent={idx}, \"{serializerName}::{fieldName}\")")) return;
        FieldSubstitution.SetEntityCallback(idx, serializerName, fieldName, callback);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: per-client callback registered for ent={Ent} \"{Ser}::{Field}\"", idx, serializerName, fieldName);
    }

    // -- SetUniform, all entities -----------------------------------------------------------------

    // Uniform (all-clients) substitution rides the per-field ENCODER hook (UniformEncoderHook): the
    // encoder fires once during the shared pack and receives the field's fieldInfo directly, so the
    // field is matched unambiguously and every client sees the value. (The bit-copy path can't reliably
    // pair a value-copy to its field, so it's reserved for per-client callbacks.) Matched by field name.

    public void SetUniform(string serializerName, string fieldName, in SpoofValue value)
    {
        if (string.IsNullOrEmpty(fieldName) || !EnsureUniformHook($"SetUniform(\"{fieldName}\")")) return;

        switch (value.Kind)
        {
            case SpoofKind.Int:
                UniformEncoderHook.SetInt(fieldName, value.RawIntBits);
                _logger.LogInformation("SendProxy: uniform int spoof \"{Field}\" → {Value}", fieldName, value.RawIntBits);
                break;
            case SpoofKind.Float:
                UniformEncoderHook.SetFloat(fieldName, value.RawFloat);
                _logger.LogInformation("SendProxy: uniform float spoof \"{Field}\" → {Value}", fieldName, value.RawFloat);
                break;
            case SpoofKind.Bool:
                UniformEncoderHook.SetBool(fieldName, value.AsBool);
                _logger.LogInformation("SendProxy: uniform bool spoof \"{Field}\" → {Value}", fieldName, value.AsBool);
                break;
            case SpoofKind.Vector:
                UniformEncoderHook.SetVector(fieldName, value.RawVec);
                _logger.LogInformation("SendProxy: uniform vector spoof \"{Field}\" → {Value}", fieldName, value.RawVec);
                break;
            case SpoofKind.String:
                UniformEncoderHook.SetString(fieldName, value.AsString);
                _logger.LogInformation("SendProxy: uniform string spoof \"{Field}\" → \"{Value}\"", fieldName, value.AsString);
                break;
            case SpoofKind.Bytes:
                UniformEncoderHook.SetBytes(fieldName, value.AsBytes);
                _logger.LogInformation("SendProxy: uniform bytes spoof \"{Field}\" ({Len} bytes)", fieldName, value.AsBytes.Length);
                break;
        }
    }

    // -- SetUniform, single entity ----------------------------------------------------------------

    public void SetUniform(IBaseEntity entity, string serializerName, string fieldName, in SpoofValue value)
    {
        if (string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName)) return;
        if (entity is not { IsValidEntity: true })
        {
            return;
        }

        var idx = (int) entity.Index;
        if (!EnsureDetours($"SetUniform(ent={idx}, \"{serializerName}::{fieldName}\")")) return;
        FieldSubstitution.SetEntitySpoof(idx, serializerName, fieldName, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        _logger.LogInformation("SendProxy: uniform spoof ent={Ent} \"{Ser}::{Field}\" → {Kind}", idx, serializerName, fieldName, value.Kind);
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
        if (client is null || entity is not { IsValidEntity: true }
            || string.IsNullOrEmpty(serializerName) || string.IsNullOrEmpty(fieldName))
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

    public void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, in SpoofValue value)
    {
        if (!BeginSendFake(client, entity, serializerName, fieldName, out var idx, out var clientPtr)) return;
        FieldSubstitution.SetOneShot(idx, serializerName, fieldName, clientPtr, value);
        FieldSubstitution.Mode = SubstitutionMode.Fake;
        entity.NetworkStateChanged(fieldName);
        _logger.LogInformation("SendProxy: one-shot fake ent={Ent} client=0x{C:X} \"{Ser}::{Field}\" → {Kind}", idx, clientPtr, serializerName, fieldName, value.Kind);
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
        if (entity is not { IsValidEntity: true })
        {
            return;
        }

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
