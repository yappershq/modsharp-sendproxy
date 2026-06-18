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

namespace YappersHQ.SendProxy.Shared;

/// <summary>
///     Identifies the networked field a proxy callback is firing for. Passed in <see cref="ProxyContext"/>.
///     The field is addressed at registration by <c>(serializerName, fieldName)</c>; this carries the
///     resolved identity the callback sees when it fires during the shared pack.
/// </summary>
public readonly struct ProxyField
{
    /// <summary>Field name, e.g. <c>"m_iHealth"</c> (inheritance-flattened — the encoder's own name).</summary>
    public string Name { get; }

    /// <summary>
    ///     The value family the field's encoder accepts. A <see cref="SpoofValue"/> written back through
    ///     <see cref="ProxyContext.SetAll"/> / <see cref="ProxyContext.SetFor"/> must be compatible with it
    ///     (a mismatch is passed through unchanged).
    /// </summary>
    public SpoofKind Kind { get; }

    public ProxyField(string name, SpoofKind kind)
    {
        Name = name;
        Kind = kind;
    }
}
