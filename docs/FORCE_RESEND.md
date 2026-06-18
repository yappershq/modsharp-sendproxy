# Force-resend (live application) — evaluated and NOT used

**Status: ABANDONED.** This document records an approach that was fully reverse-engineered but not
implemented. The production live-apply story is described below.

## Current live-apply behavior

A freshly-registered proxy on an **unchanged** field applies on the next natural re-encode of that
field (real value change or full update). To apply immediately without waiting:

- **Entity-scoped registration** (`Register(entity, …)`) — call
  `entity.NetworkStateChanged("fieldName")` immediately after registering. `NetworkStateChanged`
  sets the engine's per-field dirty bit, which schedules the field into the next snapshot's
  change-list. Because the dirty bit drives the include decision (not a value diff), it fires even
  when the real value is unchanged — i.e. it is not coalesced away. The proxy fires on the next
  pack and the substituted value goes out.

- **All-entities registration** — dirty each entity of interest with
  `entity.NetworkStateChanged("fieldName")`. The cost is one dirtied field per entity, which sends
  one extra delta; in steady state the proxy fires on every subsequent re-encode of that field.

## Why a full-update or value-change is otherwise needed

The CS2 entity delta is **value-compared**, not dirty-flag-driven at the field level.
`CNetworkGameServerBase::WriteDeltaEntity_Internal` produces the delta by comparing FROM (the
client's acked baseline) to TO (the current shared snapshot). A field that was not changed in the
shared pack — because `SetAll` wasn't called or the entity wasn't encoded this tick — is absent
from the TO snapshot's change-list and therefore not included in any client's delta. A reconnect
(full update) works because it sends every field against an empty baseline.

`NetworkStateChanged` sidesteps this by marking the field dirty regardless of value, so the
field appears in the next pack's change-list and is transmitted.

## What was evaluated (not implemented)

An earlier design ("Option D" / "force-resend") hooked the CalcDelta vtable slot and/or the
WriteFields slot of a serializer singleton (`SerializerSingleton` gamedata key) to inject a
field index into the delta list for a specific client, forcing per-client re-transmission of an
unchanged field without a full update or `NetworkStateChanged`. The RE was complete and the
mechanism confirmed, but the approach was **abandoned**: `NetworkStateChanged` combined with
entity-scoped registration provides sufficient live-apply behavior with far simpler, lower-risk
code. The `ForceResend.cs` file and `SerializerSingleton` gamedata key were removed.
