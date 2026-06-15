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

using System.Numerics;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;

namespace YappersHQ.SendProxy.Shared;

/// <summary>
///     Per-client substitution callback for an integer-family field (int / uint / bool / fixed). Runs on
///     the engine send worker threads — must be thread-safe and non-blocking. <paramref name="value"/> is
///     seeded with the registered uniform value (or 0); return <c>true</c> to encode it for this client,
///     <c>false</c> to pass the real value through. <paramref name="client"/> is the raw
///     <c>CServerSideClient*</c> recipient (0 if capture is inactive); <paramref name="entityIndex"/> is
///     the entity being sent (-1 if not captured).
/// </summary>
public delegate bool PerClientIntProxy(nint client, int entityIndex, ref int value);

/// <summary>Per-client substitution callback for a float32 field. Seeded 0f. See <see cref="PerClientIntProxy"/>.</summary>
public delegate bool PerClientFloatProxy(nint client, int entityIndex, ref float value);

/// <summary>Per-client substitution callback for a bool field. Seeded false. See <see cref="PerClientIntProxy"/>.</summary>
public delegate bool PerClientBoolProxy(nint client, int entityIndex, ref bool value);

/// <summary>
///     Per-client substitution callback for a vector / qangle field (three contiguous float32). Seeded
///     <see cref="Vector3.Zero"/>. See <see cref="PerClientIntProxy"/>.
/// </summary>
public delegate bool PerClientVectorProxy(nint client, int entityIndex, ref Vector3 value);

/// <summary>
///     Per-client substitution callback for a string field (null-terminated, byte-stream encoded). Seeded
///     with the registered uniform string (or empty). See <see cref="PerClientIntProxy"/>.
/// </summary>
public delegate bool PerClientStringProxy(nint client, int entityIndex, ref string value);

/// <summary>
///     Per-client substitution callback for a raw byte-array field (engine serializes element count then
///     count bytes). Seeded with the registered uniform array (or empty). See <see cref="PerClientIntProxy"/>.
/// </summary>
public delegate bool PerClientBytesProxy(nint client, int entityIndex, ref byte[] value);

/// <summary>
///     SendProxy for ModSharp / CS2 — intercept the per-client serialization of networked entity fields
///     and substitute the value clients receive, without changing real server-side state.
///     <para>
///         Fields are addressed by <c>(serializerName, fieldName)</c> — e.g.
///         <c>("CCSPlayerPawn", "m_iHealth")</c>. A registration is either <b>uniform</b> (every client
///         sees the same value, via <see cref="SetUniform(string, string, int)"/>) or <b>per-client</b>
///         (a callback computes the value per recipient, via <see cref="Hook(string, string, PerClientIntProxy)"/>).
///         Each form has an <see cref="IBaseEntity"/> overload that scopes the registration to a single
///         entity (per-entity wins over the all-entities registration for that entity). The substitution
///         detours install lazily on first registration.
///     </para>
/// </summary>
public interface ISendProxyManager
{
    const string Identity = nameof(ISendProxyManager);

    // -- Per-client substitution, all entities of a serializer --------------------------------------

    /// <summary>Register a per-client value callback for every entity of <paramref name="serializerName"/>.</summary>
    void Hook(string serializerName, string fieldName, PerClientIntProxy callback);

    /// <inheritdoc cref="Hook(string, string, PerClientIntProxy)"/>
    void Hook(string serializerName, string fieldName, PerClientFloatProxy callback);

    /// <inheritdoc cref="Hook(string, string, PerClientIntProxy)"/>
    void Hook(string serializerName, string fieldName, PerClientBoolProxy callback);

    /// <inheritdoc cref="Hook(string, string, PerClientIntProxy)"/>
    void Hook(string serializerName, string fieldName, PerClientVectorProxy callback);

    /// <inheritdoc cref="Hook(string, string, PerClientIntProxy)"/>
    void Hook(string serializerName, string fieldName, PerClientStringProxy callback);

    /// <inheritdoc cref="Hook(string, string, PerClientIntProxy)"/>
    void Hook(string serializerName, string fieldName, PerClientBytesProxy callback);

    // -- Per-client substitution, single entity -----------------------------------------------------

    /// <summary>Register a per-client value callback scoped to a single <paramref name="entity"/>.</summary>
    void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientIntProxy callback);

    /// <inheritdoc cref="Hook(IBaseEntity, string, string, PerClientIntProxy)"/>
    void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientFloatProxy callback);

    /// <inheritdoc cref="Hook(IBaseEntity, string, string, PerClientIntProxy)"/>
    void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientBoolProxy callback);

    /// <inheritdoc cref="Hook(IBaseEntity, string, string, PerClientIntProxy)"/>
    void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientVectorProxy callback);

    /// <inheritdoc cref="Hook(IBaseEntity, string, string, PerClientIntProxy)"/>
    void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientStringProxy callback);

    /// <inheritdoc cref="Hook(IBaseEntity, string, string, PerClientIntProxy)"/>
    void Hook(IBaseEntity entity, string serializerName, string fieldName, PerClientBytesProxy callback);

    // -- Uniform substitution, all entities ---------------------------------------------------------

    /// <summary>Make every client see <paramref name="value"/> for this field on every entity of the serializer.</summary>
    void SetUniform(string serializerName, string fieldName, int value);

    /// <inheritdoc cref="SetUniform(string, string, int)"/>
    void SetUniform(string serializerName, string fieldName, float value);

    /// <inheritdoc cref="SetUniform(string, string, int)"/>
    void SetUniform(string serializerName, string fieldName, bool value);

    /// <inheritdoc cref="SetUniform(string, string, int)"/>
    void SetUniform(string serializerName, string fieldName, Vector3 value);

    /// <inheritdoc cref="SetUniform(string, string, int)"/>
    void SetUniform(string serializerName, string fieldName, string value);

    /// <inheritdoc cref="SetUniform(string, string, int)"/>
    void SetUniform(string serializerName, string fieldName, byte[] value);

    // -- Uniform substitution, single entity --------------------------------------------------------

    /// <summary>Make every client see <paramref name="value"/> for this field on a single <paramref name="entity"/>.</summary>
    void SetUniform(IBaseEntity entity, string serializerName, string fieldName, int value);

    /// <inheritdoc cref="SetUniform(IBaseEntity, string, string, int)"/>
    void SetUniform(IBaseEntity entity, string serializerName, string fieldName, float value);

    /// <inheritdoc cref="SetUniform(IBaseEntity, string, string, int)"/>
    void SetUniform(IBaseEntity entity, string serializerName, string fieldName, bool value);

    /// <inheritdoc cref="SetUniform(IBaseEntity, string, string, int)"/>
    void SetUniform(IBaseEntity entity, string serializerName, string fieldName, Vector3 value);

    /// <inheritdoc cref="SetUniform(IBaseEntity, string, string, int)"/>
    void SetUniform(IBaseEntity entity, string serializerName, string fieldName, string value);

    /// <inheritdoc cref="SetUniform(IBaseEntity, string, string, int)"/>
    void SetUniform(IBaseEntity entity, string serializerName, string fieldName, byte[] value);

    // -- SendFake (one-shot push to a single client) ------------------------------------------------

    /// <summary>
    ///     Push <paramref name="value"/> for <paramref name="serializerName"/>::<paramref name="fieldName"/>
    ///     on <paramref name="entity"/> to a single <paramref name="client"/> on the next snapshot, then
    ///     stop — without changing real server state. Unlike <see cref="Hook(IBaseEntity, string, string, PerClientIntProxy)"/>
    ///     this does not persist: it fires exactly once for that client. The field is force-dirtied so it
    ///     re-transmits immediately even if its real value did not change, which is the intended use —
    ///     "send a fake now, optionally <c>Hook</c> afterwards to keep it faked".
    /// </summary>
    void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, int value);

    /// <inheritdoc cref="SendFake(IGameClient, IBaseEntity, string, string, int)"/>
    void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, float value);

    /// <inheritdoc cref="SendFake(IGameClient, IBaseEntity, string, string, int)"/>
    void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, bool value);

    /// <inheritdoc cref="SendFake(IGameClient, IBaseEntity, string, string, int)"/>
    void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, Vector3 value);

    /// <inheritdoc cref="SendFake(IGameClient, IBaseEntity, string, string, int)"/>
    void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, string value);

    /// <inheritdoc cref="SendFake(IGameClient, IBaseEntity, string, string, int)"/>
    void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, byte[] value);

    // -- Removal ------------------------------------------------------------------------------------

    /// <summary>Remove the all-entities registration for <paramref name="serializerName"/>::<paramref name="fieldName"/>.</summary>
    void Unhook(string serializerName, string fieldName);

    /// <summary>Remove the registration scoped to <paramref name="entity"/> for this field.</summary>
    void Unhook(IBaseEntity entity, string serializerName, string fieldName);

    /// <summary>Remove every registration and uninstall the substitution detours. Call on plugin shutdown.</summary>
    void UnhookAll();

    /// <summary>True if any (uniform or per-client) registration exists for this field across all entities.</summary>
    bool IsHooked(string serializerName, string fieldName);
}
