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
using System.Text;
using Sharp.Shared.Objects;
using YappersHQ.SendProxy.Shared;

namespace YappersHQ.SendProxy.Native;

// Static store of registered proxies, keyed (serializer, field, entityIndex). entityIndex -1 = all entities
// of that serializer. ProxyManager (the IProxyManager impl) writes it; the encoder dispatch (static hook)
// reads it. A registration carries the callback + the owning module assembly (to purge on module unload —
// invoking a delegate into an unloaded ALC would crash the engine encode thread).
//
// The hot encode/send path NEVER builds a managed string: registered names are kept as UTF8 byte[], and
// matching is done by comparing the engine's stable name char* against those bytes (NativeUtil.NameEquals),
// with the result cached by pointer. The registered string is reused (not re-allocated) for the consumer's
// ProxyField.Name.
internal static class ProxyRegistry
{
    internal readonly struct Entry
    {
        public readonly ProxyCallback Callback;
        public readonly string?       Owner;
        public readonly string        Serializer; // registered name, reused for ProxyField.Name (no new string)
        public readonly string        Field;
        public readonly byte[]        SerUtf8;     // for char*-vs-bytes matching on the hot path
        public readonly byte[]        FieldUtf8;
        public readonly int           Ent;

        public Entry(string ser, string field, int ent, ProxyCallback callback)
        {
            Callback   = callback;
            Owner      = callback.Method.Module.Assembly.GetName().Name;
            Serializer = ser;
            Field      = field;
            SerUtf8    = Encoding.UTF8.GetBytes(ser);
            FieldUtf8  = Encoding.UTF8.GetBytes(field);
            Ent        = ent;
        }

        public bool Found => Callback is not null;
    }

    private static readonly ConcurrentDictionary<(string ser, string field, int ent), Entry> _registry = new();

    // ── Pointer-resolution caches: avoid building a managed string per field per recipient on the hot path.
    // Cleared on any registration change (OnMutated) and on level activation (serializer metadata can be
    // rebuilt → name char* reused for a different name). All are cheap to repopulate lazily.
    private static readonly ConcurrentDictionary<nint, bool>                       _serHasProxy = new();
    private static readonly ConcurrentDictionary<(nint ser, nint fld), Entry>      _globalByPtr = new();
    private static readonly ConcurrentDictionary<(nint ser, nint fld, int ent), Entry> _entByPtr = new();
    private static volatile bool _hasEntityScoped;

    public static bool IsEmpty => _registry.IsEmpty;

    public static void Set(string ser, string field, int ent, ProxyCallback cb)
    {
        _registry[(ser, field, ent)] = new Entry(ser, field, ent, cb);
        OnMutated();
    }

    public static void Remove(string ser, string field, int ent)
    {
        if (_registry.TryRemove((ser, field, ent), out _))
        {
            OnMutated();
        }
    }

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

    // ── Hot path: match by the engine's stable name char* via byte-compare, cache by pointer. ──

    // Fast-path gate: does ANY registration target this serializer? Resolved ONCE per entity (at encode
    // capture) so the per-field hook can skip all per-field work for serializers no proxy touches.
    public static bool HasSerializerPtr(nint serNamePtr)
    {
        if (serNamePtr == 0)
        {
            return false;
        }

        if (_serHasProxy.TryGetValue(serNamePtr, out var cached))
        {
            return cached;
        }

        var any = false;
        foreach (var e in _registry.Values)
        {
            if (NativeUtil.NameEquals(serNamePtr, e.SerUtf8))
            {
                any = true;

                break;
            }
        }

        _serHasProxy[serNamePtr] = any;

        return any;
    }

    // Resolve the registration for (serializerNamePtr, fieldNamePtr, entityIndex). Entity-scoped wins over
    // the all-entities (-1) one. Pure pointer lookups after first sight (byte-compare on cache miss only).
    public static bool TryGetByPtr(nint serNamePtr, nint fieldNamePtr, int ent, out Entry entry)
    {
        if (_hasEntityScoped && ent >= 0)
        {
            var ekey = (serNamePtr, fieldNamePtr, ent);
            if (!_entByPtr.TryGetValue(ekey, out var ee))
            {
                ee            = Resolve(serNamePtr, fieldNamePtr, ent);
                _entByPtr[ekey] = ee;
            }

            if (ee.Found)
            {
                entry = ee;

                return true;
            }
        }

        var gkey = (serNamePtr, fieldNamePtr);
        if (!_globalByPtr.TryGetValue(gkey, out var ge))
        {
            ge               = Resolve(serNamePtr, fieldNamePtr, -1);
            _globalByPtr[gkey] = ge;
        }

        entry = ge;

        return ge.Found;
    }

    // Byte-compare scan over registrations for an exact (ser, field, ent) match. Cold (cache-miss only).
    private static Entry Resolve(nint serNamePtr, nint fieldNamePtr, int ent)
    {
        foreach (var e in _registry.Values)
        {
            if (e.Ent == ent
                && NativeUtil.NameEquals(serNamePtr, e.SerUtf8)
                && NativeUtil.NameEquals(fieldNamePtr, e.FieldUtf8))
            {
                return e;
            }
        }

        return default; // Found == false
    }

    // Recompute the entity-scoped flag + drop the pointer caches. Called on every registration change
    // (rare). The caches are also dropped on level activation via ClearPtrCaches.
    private static void OnMutated()
    {
        var anyEnt = false;
        foreach (var key in _registry.Keys)
        {
            if (key.ent >= 0)
            {
                anyEnt = true;

                break;
            }
        }

        _hasEntityScoped = anyEnt;
        ClearPtrCaches();
    }

    // Drop the pointer-resolution caches (serializer metadata may have been rebuilt — a name char* could now
    // belong to a different name). Names simply re-resolve by byte-compare on next use.
    public static void ClearPtrCaches()
    {
        _serHasProxy.Clear();
        _globalByPtr.Clear();
        _entByPtr.Clear();
    }

    // Drop every registration scoped to an entity index (entity created/deleted — indices are reused).
    public static void ClearEntity(int ent)
    {
        if (ent < 0)
        {
            return;
        }

        var removed = false;
        foreach (var key in _registry.Keys)
        {
            if (key.ent == ent && _registry.TryRemove(key, out _))
            {
                removed = true;
            }
        }

        if (removed)
        {
            OnMutated();
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

        if (removed > 0)
        {
            OnMutated();
        }

        return removed;
    }

    public static void Clear()
    {
        _registry.Clear();
        OnMutated();
    }

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
