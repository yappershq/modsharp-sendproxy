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

using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;

namespace YappersHQ.SendProxy.Shared;

/// <summary>
///     Per-client substitution callback. Runs on the engine send worker threads — must be thread-safe and
///     non-blocking, and should only READ from <paramref name="client"/>/<paramref name="entity"/> (slot,
///     index, steamid, schema fields), never mutate engine state from here. <paramref name="value"/> is
///     seeded with the registered uniform value (or the zero value for that kind); mutate it and return
///     <c>true</c> to encode the mutated value for this client, or return <c>false</c> to pass the real
///     value through. <paramref name="client"/> is the recipient being encoded; <paramref name="entity"/>
///     is the entity whose field is being sent.
/// </summary>
public delegate bool SendProxyCallback(IGameClient client, IBaseEntity entity, ref SpoofValue value);

/// <summary>
///     SendProxy for ModSharp / CS2 — intercept the per-client serialization of networked entity fields
///     and substitute the value clients receive, without changing real server-side state.
///     <para>
///         Fields are addressed by <c>(serializerName, fieldName)</c> — e.g.
///         <c>("CCSPlayerPawn", "m_iHealth")</c>. A registration is either <b>uniform</b> (every client
///         sees the same value, via <see cref="SetUniform(string, string, in SpoofValue)"/>) or
///         <b>per-client</b> (a callback computes the value per recipient, via
///         <see cref="Hook(string, string, SendProxyCallback)"/>). Each form has an
///         <see cref="IBaseEntity"/> overload that scopes the registration to a single entity (per-entity
///         wins over the all-entities registration for that entity). The substitution detours install
///         lazily on first registration.
///     </para>
/// </summary>
public interface ISendProxyManager
{
    const string Identity = nameof(ISendProxyManager);

    // -- Per-client substitution, all entities of a serializer ----------------------------------------

    /// <summary>
    ///     Register a per-client value callback for every entity of <paramref name="serializerName"/>.
    ///     The callback receives a <see cref="SpoofValue"/> seeded with the registered uniform value (or
    ///     the zero value for that kind); mutate it and return <c>true</c> to apply. The callback kind
    ///     (determined by <see cref="SpoofValue.Kind"/> on the first call) must be compatible with the
    ///     field's encoder family — a mismatch is silently passed through. Thread-safe: registered on the
    ///     game thread, invoked on engine send worker threads.
    /// </summary>
    void Hook(string serializerName, string fieldName, SendProxyCallback callback);

    /// <summary>Register a per-client value callback scoped to a single <paramref name="entity"/>.</summary>
    void Hook(IBaseEntity entity, string serializerName, string fieldName, SendProxyCallback callback);

    // -- Uniform substitution, all entities -----------------------------------------------------------

    /// <summary>
    ///     Make every client see <paramref name="value"/> for this field on every entity of the serializer.
    ///     The <see cref="SpoofValue.Kind"/> determines the encoder family — it must match the field's
    ///     actual encoder or the value will be passed through.
    /// </summary>
    void SetUniform(string serializerName, string fieldName, in SpoofValue value);

    // -- Uniform substitution, single entity ----------------------------------------------------------

    /// <summary>
    ///     Make every client see <paramref name="value"/> for this field on a single
    ///     <paramref name="entity"/>. Per-entity wins over the all-entities registration for that entity.
    /// </summary>
    void SetUniform(IBaseEntity entity, string serializerName, string fieldName, in SpoofValue value);

    // -- SendFake (one-shot push to a single client) --------------------------------------------------

    /// <summary>
    ///     Push <paramref name="value"/> for <paramref name="serializerName"/>::<paramref name="fieldName"/>
    ///     on <paramref name="entity"/> to a single <paramref name="client"/> on the next snapshot, then
    ///     stop — without changing real server state. Unlike
    ///     <see cref="Hook(IBaseEntity, string, string, SendProxyCallback)"/> this does not persist: it
    ///     fires exactly once for that client. The field is force-dirtied so it re-transmits immediately
    ///     even if its real value did not change, which is the intended use — "send a fake now, optionally
    ///     <c>Hook</c> afterwards to keep it faked".
    /// </summary>
    void SendFake(IGameClient client, IBaseEntity entity, string serializerName, string fieldName, in SpoofValue value);

    // -- Removal --------------------------------------------------------------------------------------

    /// <summary>Remove the all-entities registration for <paramref name="serializerName"/>::<paramref name="fieldName"/>.</summary>
    void Unhook(string serializerName, string fieldName);

    /// <summary>Remove the registration scoped to <paramref name="entity"/> for this field.</summary>
    void Unhook(IBaseEntity entity, string serializerName, string fieldName);

    /// <summary>Remove every registration and uninstall the substitution detours. Call on plugin shutdown.</summary>
    void UnhookAll();

    /// <summary>True if any (uniform or per-client) registration exists for this field across all entities.</summary>
    bool IsHooked(string serializerName, string fieldName);

    /// <summary>
    ///     Enable/disable live force-resend: pushes hooked fields into the per-client delta so a spoof applies
    ///     immediately instead of only after a full update. Off by default; installs a vtable hook on the
    ///     serializer's encode path when first enabled. Returns false if the hook could not be installed.
    ///     (See docs/FORCE_RESEND.md — the field-index numbering is verified on first enable.)
    /// </summary>
    bool SetForceResend(bool enabled);
}
