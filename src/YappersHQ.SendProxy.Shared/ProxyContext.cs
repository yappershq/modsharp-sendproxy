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

using System.Collections.Generic;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;

namespace YappersHQ.SendProxy.Shared;

/// <summary>
///     The single argument to a <see cref="ProxyCallback"/>. Fired <b>once per (entity, field)</b> during
///     the shared <c>PackEntities</c> pass — never per recipient — so a callback's cost is O(1) in player
///     count. The one call services every recipient:
///     <list type="bullet">
///         <item>
///             <see cref="SetAll"/> — every client sees the value. It is written into the shared snapshot,
///             so it enters each client's delta naturally (no re-send hackery) and costs nothing extra.
///         </item>
///         <item>
///             <see cref="SetFor"/> — a specific client sees a different value; clients not given an
///             override see the real value. Used for per-viewer effects (e.g. an ESP-style highlight only
///             one player sees). Applied natively at the per-client write — still one managed call here.
///         </item>
///     </list>
///     <para>
///         Runs on a pack worker thread. It is never invoked concurrently for the same (entity, field), so
///         there is no race on its own work, but the callback MUST NOT call main-thread-only engine APIs or
///         mutate shared state unguarded — read schema / compute values only.
///     </para>
/// </summary>
public ref struct ProxyContext
{
    /// <summary>The entity being packed.</summary>
    public IBaseEntity Entity { get; }

    /// <summary>The field this callback is firing for.</summary>
    public ProxyField Field { get; }

    /// <summary>The field's real value (what the engine would send without a proxy).</summary>
    public SpoofValue Original { get; }

    // Result the framework reads after the callback returns. Internal — only the SendProxy module (granted
    // via InternalsVisibleTo) constructs the context and reads these back.
    internal SpoofValue UniformValue;
    internal bool       HasUniform;

    // Per-recipient overrides. The buffer is supplied (and pooled/cleared) by the framework so a uniform
    // callback — the common case, which never touches this — allocates nothing.
    private readonly List<(IGameClient Client, SpoofValue Value)> _perClient;

    internal ProxyContext(
        IBaseEntity                                   entity,
        ProxyField                                    field,
        in SpoofValue                                 original,
        List<(IGameClient Client, SpoofValue Value)> perClientBuffer)
    {
        Entity       = entity;
        Field        = field;
        Original     = original;
        _perClient   = perClientBuffer;
        UniformValue = default;
        HasUniform   = false;
    }

    /// <summary>Every client sees <paramref name="value"/>. Goes into the shared snapshot (O(1), live).</summary>
    public void SetAll(in SpoofValue value)
    {
        UniformValue = value;
        HasUniform   = true;
    }

    /// <summary>
    ///     Only <paramref name="client"/> sees <paramref name="value"/>; clients without an override see the
    ///     real value. For per-viewer effects. May be called for multiple clients in the same invocation.
    /// </summary>
    public void SetFor(IGameClient client, in SpoofValue value)
        => _perClient.Add((client, value));

    /// <summary>Framework: whether any per-recipient override was set.</summary>
    internal readonly bool HasPerClient => _perClient.Count > 0;

    /// <summary>Framework: the per-recipient overrides recorded this call.</summary>
    internal readonly List<(IGameClient Client, SpoofValue Value)> PerClientValues => _perClient;
}
