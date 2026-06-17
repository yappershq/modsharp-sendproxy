# Forcing a per-client spoof to apply LIVE (force-resend) — RE + design

Status: RE complete, mechanism confirmed. Implementation is engine surgery that must be validated on a
live client (a connected player) — documented here as a precise spec to prototype against, not yet shipped.

## The problem

Per-client value substitution (the working hook) fires during the per-client field copy
(`BitCopyPrimitive`) and fakes a field's value for one recipient. It works — but only for fields that are
**in that client's delta**. Live, a freshly-registered spoof on an *unchanged* field does not show until a
**full update** (e.g. reconnect). prefix's symptom: "only shows after connect."

## Why `NetworkStateChanged` alone does NOT fix it (confirmed by RE + live diagnostic)

The CS2 entity delta is **value-compared**, not dirty-flag-driven at the field level.

Live diagnostic (ttt, 52000 BitCopy fires): `_currentSerializer` was set on **100%** of fires
(`serSet=52000, noSer=0`) — i.e. the substitution pipeline is fully active on live deltas, there is NO
coverage gap. Yet the spoofed fields (health/colour/glow) never appeared in the live stream — only
movement fields did. So the fields simply weren't *in* the delta.

RE of `CNetworkGameServerBase::WriteDeltaEntity_Internal` (libengine2.so `FUN_007d15c0` / engine2.dll
`0x1800d1240`) shows the delta is produced by a vtable call:

```
(*(code**)(*DAT_00ac4ae0 + 0x40))(
    DAT_00ac4ae0,
    **(long**)(param_2 + 0x90) + 0x18,           // FROM snapshot field-data (client's acked baseline)
    (*(long**)(param_2 + 0x90))[4],              // FROM count
    *(long*)(*(long*)(param_2 + 0x98) + 0x20),   // TO snapshot field-data (current shared pack)
    piVar16,                                     // OUT: field-index list = the fields that DIFFER
    *(int*)(param_2 + 0x34),                     // entity index
    0, ctx)
```

`param_2+0x90` = FROM (per-client baseline), `param_2+0x98` = TO (shared pack). The fn emits the list of
fields whose serialized bits **differ** between FROM and TO. Since the substitution keeps the *real* value
unchanged, FROM == TO for the spoofed field → it is excluded from the delta → `NetworkStateChanged` (which
only marks the entity for re-pack) cannot force it in. A full update works only because it sends every
field against an empty baseline.

## Options (lightest-first), with trade-offs

- **D — CalcDelta field-list injection (recommended, surgical).** Hook the `+0x40` CalcDelta fn; after it
  runs, for the *issuer* client (RecipientCapture) + a hooked entity, APPEND the hooked field's index to
  the output list (`piVar16`: count + index array) if absent. The field then gets encoded for that client
  and the value-hook fakes it. Per-field, per-client, no value change, no full update, works per-tick
  (so cycling values work too). Cost: one list check/append per hooked field per client.
  Needs: (a) hook the vtable fn (resolve `DAT_00ac4ae0` + slot 0x40, or sig the call site — cross-platform
  via the global), (b) the issuer + entity-index (both already captured), (c) **field-name → serializer
  field-index** resolution (the one missing piece — resolvable from the entity's flattened serializer; can
  be cached per-serializer from the full-update path where the serializer is in scope).

- **A — per-entity FROM-baseline reset.** Make CalcDelta see an empty FROM for one entity → it re-sends all
  of that entity's fields to that one client (not a global full update). Lighter than full-update, but
  per-entity (not per-field) and risks crashing if the FROM pointer is deref'd elsewhere — must reset the
  client's acked-baseline for the entity cleanly, not null a live pointer. Needs per-client baseline RE.

- **B — TransmitManager PVS toggle.** `ITransmitManager.SetEntityState(ent, client, false)` then `true`
  forces a full entity re-send to that client. Works for non-pawn entities; **blocked for PlayerPawn**
  (ModSharp rejects pawn transmit — see memory). Causes a client-side entity delete+recreate blip. Quick
  (existing API, no RE) but not clean for the player-pawn demos.

- **C — accept + document.** Spoof applies on the field's next natural transmission (real value change) or
  full update. For instant apply, caller uses `IGameClient.ForceFullUpdate()` (heavy — rejected by prefix).

## Recommendation

Implement **D** (gated behind a flag, off by default) and validate on a live client. It is the only option
that is light, per-field, per-client, value-preserving, and cross-platform (the CalcDelta fn resolves from
a global on both builds). The single unsolved sub-problem is field-name → serializer field-index; resolve
it by caching the mapping per flattened-serializer during the full-update path (where the serializer is in
scope via the WriteFieldList shim), keyed by serializer name, then look it up in the CalcDelta hook.

Cycling values (e.g. sp_fakeaim's rainbow colour) work automatically under D since the field is re-injected
every tick it's hooked.
