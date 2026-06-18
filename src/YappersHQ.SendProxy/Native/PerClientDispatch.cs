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

// Per-viewer override table for the SetFor path. A proxy that calls ctx.SetFor records, during the shared
// pack, the per-recipient values here; the per-client copy stage then applies each client's value (or the
// default — uniform value if also SetAll'd, else the real value — to restore non-recipients). The shared
// snapshot is written with a value that differs from the real one so the field enters every client's delta,
// then this table corrects each recipient at the per-client copy. Keyed (entityIndex, fieldName); overwritten
// each pack of that entity, so it always reflects the current snapshot.
//
// NOTE: the CONSUME side (applying these at the per-client copy) is wired together with the teardown of the
// legacy per-client BitCopy hook (they share that hook site) — pending the uniform-core live confirmation.
// Recording here is harmless until then (an unconsumed table has no effect).
internal static class PerClientDispatch
{
    internal readonly struct Rec
    {
        public readonly FieldType                  Type;
        public readonly SpoofValue                 Default;     // value non-recipients receive (uniform or real)
        public readonly (int Slot, SpoofValue Val)[] Overrides; // per-recipient values, by engine slot

        public Rec(FieldType type, in SpoofValue def, (int, SpoofValue)[] overrides)
        {
            Type      = type;
            Default   = def;
            Overrides = overrides;
        }
    }

    // Keyed by (entityIndex, field NAME POINTER) — the engine's stable char* — so neither the record (pack)
    // nor the resolve (per-client send) path ever builds a managed string.
    private static readonly ConcurrentDictionary<(int ent, nint field), Rec> _table = new();

    public static bool IsEmpty => _table.IsEmpty;

    // Record the per-recipient overrides produced by a SetFor proxy this pack. Converts IGameClient → slot
    // up front so the per-client copy (which only cheaply knows the recipient slot) needs no managed lookup.
    public static void Record(int entityIndex, nint field, FieldType type, in SpoofValue def,
        List<(IGameClient Client, SpoofValue Value)> overrides)
    {
        if (entityIndex < 0 || field == 0 || overrides.Count == 0)
        {
            return;
        }

        // Reuse the stored array in place when the recipient count is unchanged (the steady state — same
        // players tick to tick), so this path allocates nothing across ticks. Safe because Record and
        // TryResolve never run concurrently: both happen inside one synchronous per-tick
        // CNetworkGameServer::SendClientMessages call — the PackEntities parallel-for fully JOINS (its
        // results are consumed inline right after the thread-pool dispatch returns) before the per-client
        // send, and that send is a plain synchronous do/while over clients (no async/threaded dispatch), so
        // it completes before the call returns. Tick N+1 (next Record) cannot begin until tick N's send
        // (last TryResolve) has returned on the same host thread. Verified by RE of libengine2.so
        // (FUN_007a7280): pack dispatch + inline join, then the synchronous client loop. See docs.
        var key = (entityIndex, field);

        (int Slot, SpoofValue Val)[] arr;
        if (_table.TryGetValue(key, out var existing) && existing.Overrides.Length == overrides.Count)
        {
            arr = existing.Overrides;
        }
        else
        {
            arr = new (int, SpoofValue)[overrides.Count];
        }

        for (var i = 0; i < overrides.Count; i++)
        {
            arr[i] = (overrides[i].Client.Slot.AsPrimitive(), overrides[i].Value);
        }

        _table[key] = new Rec(type, in def, arr);
    }

    // Resolve the value a given recipient slot should receive for (entity, field): its override if present,
    // else the default. Returns false if no per-viewer record exists (caller leaves the value untouched).
    public static bool TryResolve(int entityIndex, nint field, int slot, out SpoofValue value, out FieldType type)
    {
        value = default;
        type  = FieldType.Unsupported;
        if (!_table.TryGetValue((entityIndex, field), out var rec))
        {
            return false;
        }

        type = rec.Type;
        foreach (var (s, v) in rec.Overrides)
        {
            if (s == slot)
            {
                value = v;

                return true;
            }
        }

        value = rec.Default;

        return true;
    }

    public static void ClearEntity(int entityIndex)
    {
        if (entityIndex < 0)
        {
            return;
        }

        foreach (var key in _table.Keys)
        {
            if (key.ent == entityIndex)
            {
                _table.TryRemove(key, out _);
            }
        }
    }

    public static void Clear()
        => _table.Clear();
}
