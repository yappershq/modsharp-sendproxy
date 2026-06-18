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

namespace YappersHQ.SendProxy.Shared;

/// <summary>
///     A field proxy. Fired <b>once per (entity, field)</b> during the shared <c>PackEntities</c> pass to
///     decide the value clients receive for that field, without changing real server state. See
///     <see cref="ProxyContext"/> for the SetAll (uniform) / SetFor (per-viewer) contract and the
///     thread-safety rules.
/// </summary>
public delegate void ProxyCallback(ref ProxyContext ctx);

/// <summary>
///     SendProxy for ModSharp / CS2 — register field proxies that decide, per packed entity, the values
///     clients receive for a networked field. The callback fires once per field during the shared pack
///     (O(1) in player count, race-free by construction), which is the model the ModSharp core expects.
///     Fields are addressed by <c>(serializerName, fieldName)</c>, e.g. <c>("CCSPlayerPawn","m_iHealth")</c>.
///     <para>
///         <see cref="ProxyContext.SetAll"/> spoofs the value for every client (written into the shared
///         snapshot); <see cref="ProxyContext.SetFor"/> spoofs it for specific recipients only.
///     </para>
/// </summary>
public interface IProxyManager
{
    const string Identity = nameof(IProxyManager);

    /// <summary>Register a proxy for every entity of <paramref name="serializerName"/>.</summary>
    void Register(string serializerName, string fieldName, ProxyCallback callback);

    /// <summary>Register a proxy scoped to a single <paramref name="entity"/> (wins over the all-entities proxy).</summary>
    void Register(IBaseEntity entity, string serializerName, string fieldName, ProxyCallback callback);

    /// <summary>Remove the all-entities proxy for this field.</summary>
    void Unregister(string serializerName, string fieldName);

    /// <summary>Remove the proxy scoped to <paramref name="entity"/> for this field.</summary>
    void Unregister(IBaseEntity entity, string serializerName, string fieldName);

    /// <summary>True if any proxy (all-entities or entity-scoped) is registered for this field.</summary>
    bool IsRegistered(string serializerName, string fieldName);
}
