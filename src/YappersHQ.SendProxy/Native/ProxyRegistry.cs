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
using System.Collections.Concurrent;
using Sharp.Shared.Objects;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Native;

// Static store of registered proxies, keyed (serializer, field, entityIndex). entityIndex -1 = all entities
// of that serializer. The encoder dispatch (static hook) reads this; ProxyManager (the IProxyManager impl)
// writes it. A registration carries the callback + the owning module assembly (to purge on module unload —
// invoking a delegate into an unloaded ALC would crash the engine encode thread).
internal static class ProxyRegistry
{
    internal readonly struct Entry
    {
        public readonly ProxyCallback Callback;
        public readonly string?       Owner;

        public Entry(ProxyCallback callback)
        {
            Callback = callback;
            Owner    = callback.Method.Module.Assembly.GetName().Name;
        }
    }

    private static readonly ConcurrentDictionary<(string ser, string field, int ent), Entry> _registry = new();

    public static bool IsEmpty => _registry.IsEmpty;

    public static void Set(string ser, string field, int ent, ProxyCallback cb)
        => _registry[(ser, field, ent)] = new Entry(cb);

    public static void Remove(string ser, string field, int ent)
        => _registry.TryRemove((ser, field, ent), out _);

    public static bool Has(string ser, string field)
    {
        foreach (var key in _registry.Keys)
        {
            if (key.ser == ser && key.field == field)
            {
                return true;
            }
        }

        return false;
    }

    // Entity-scoped registration wins over the all-entities (-1) one for that entity.
    public static bool TryGet(string ser, string field, int ent, out Entry entry)
    {
        if (ent >= 0 && _registry.TryGetValue((ser, field, ent), out entry))
        {
            return true;
        }

        return _registry.TryGetValue((ser, field, -1), out entry);
    }

    // Drop every registration scoped to an entity index (entity created/deleted — indices are reused).
    public static void ClearEntity(int ent)
    {
        if (ent < 0)
        {
            return;
        }

        foreach (var key in _registry.Keys)
        {
            if (key.ent == ent)
            {
                _registry.TryRemove(key, out _);
            }
        }
    }

    // Drop every registration owned by an unloading module.
    public static int PurgeOwner(string moduleName)
    {
        var removed = 0;
        foreach (var kv in _registry)
        {
            if (kv.Value.Owner == moduleName && _registry.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    public static void Clear()
        => _registry.Clear();

    // Per-recipient override buffer for ProxyContext.SetFor — pooled per encoding thread so the common
    // uniform callback (which never calls SetFor) allocates nothing. Cleared before each callback.
    [System.ThreadStatic]
    private static List<(IGameClient Client, SpoofValue Value)>? _perClientBuffer;

    public static List<(IGameClient Client, SpoofValue Value)> RentPerClientBuffer()
    {
        var buf = _perClientBuffer ??= new List<(IGameClient, SpoofValue)>(8);
        buf.Clear();

        return buf;
    }
}
