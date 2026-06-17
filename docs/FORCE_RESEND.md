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

## Option D — implementation anchors (RE'd, ready to build + validate live)

The CalcDelta call inside `WriteDeltaEntity_Internal` (Linux libengine2 `0x7d15c0`) is a **virtual call on a
serializer singleton**, not a free function:

```
007d19c8  MOV RDI, [0x00ac4ae0]   ; RDI = serializer singleton (global holds the object ptr)
007d19da  MOV RAX, [RDI]          ; RAX = its vtable
007d19f5  CALL [RAX + 0x40]       ; CalcDelta = vtable slot 8  (0x40 / 8)
```

So the hook target = **vtable slot 8 of `*(global @ 0x00ac4ae0)`**. Hook it with ModSharp `IVirtualHook`
(hook a vtable slot of the live object), resolved at boot as: sig the global-load instruction →
`+3 r` factory (skip the 3-byte `48 8B 3D` opcode, RIP-resolve the rel32) → gives the global slot address
`0x00ac4ae0` → read `*slot` = the object → `IVirtualHook` on its vtable index 8.

Linux sig for the global load (distinctive run; rel32 wildcarded):
`48 8B 3D ? ? ? ? 48 8B 30 48 8B 4A 20 48 8B 50 20 48 8B 07`  (`+3 r` factory).
Windows: derive the equivalent global-load sig in engine2.dll's WriteDeltaEntity_Internal (`0x1800d1240`)
and the same vtable-slot-8 hook applies (slot index is layout-stable cross-platform).

Hook body: call original CalcDelta (it fills the output field-index list), then if
`RecipientCapture.CurrentClient` is an issuer with a registration for this entity index (the entity index
is in the call args), and the hooked field's serializer-index isn't already present, **append it** to the
list (bump the count). The value-substitution hook then fakes it during the subsequent field write.

Field-name → serializer-field-index: build the map once from the full-update path — the WriteFieldList
shim already has the per-entity serializer in scope (`_currentSerializer`, records at +0x30); walk it,
record `fieldName → index` keyed by serializer name, and look it up in the CalcDelta hook.

### The output field-index list format (RE'd — so the append is safe)

CalcDelta's 5th arg (the output list, `param_2+0x900` inside WriteDeltaEntity_Internal) is a
**`CUtlVector<int>`** of field indices:

```
+0x00  int   count          // number of field indices written
+0x04  int   sizeAndFlag     // capacity; (& 0x7fffffff) = size, high bit (0x80000000) = heap-allocated
+0x08  int[4] inline   OR    // when size <= 4: indices stored inline here
+0x08  int*  heapPtr         // when size  > 4: pointer to the index array (read as *(int**)(list+8))
```

(Confirmed by the consumption in WriteDeltaEntity_Internal: `*piVar16` is the count, `piVar16[1] &
0x7fffffff` the size with the external-alloc bit, `piVar9 = (size>4) ? *(int**)(piVar16+2) : piVar16+2`.)

To inject the hooked field: standard `CUtlVector<int>` push-back of the field's serializer-index —
grow if needed (respect the inline-≤4 / heap->4 split + the 0x80000000 flag), write the index, bump count.
Skip if the index is already present (CalcDelta may have included it from a real change).

Ship gated behind a flag (off by default) and validate on a live client before enabling — appending to the
engine's delta list is hot-path engine surgery that must be confirmed in-game, not assumed.

### CORRECTION (deeper RE) — hook the consumer `+0x70`, not CalcDelta `+0x40`

WriteDeltaEntity_Internal builds the field-index list via **two** paths, both writing the same
`CUtlVector<int>` (`param_2+0x900`):
- **change-tick filter** (manual build, lines ~78-180): walks the TO-snapshot's per-field change list
  (`*(to+0x10)` → entries `{int fieldIndex @+0, int changeTick @+4}`, stride 8) and adds fields whose
  `changeTick > baselineTick` (`*(*(param_2+0xa0)+4)`). The appended element = `*record` = the field index.
- **value-compare** (the `+0x40` CalcDelta call, line ~192) for the other branch.

Both then feed the list to the **encode/WriteFields fn = vtable slot 14 (`+0x70`)**:
```
(*(code**)(*DAT_00ac4ae0 + 0x70))(DAT_00ac4ae0, FROM-data=*(param_2+0x90)+0x18, count, piVar16 /*arg3*/, …)
```
`+0x70` consumes `piVar16` (the field-index list) and encodes each listed field — that's where
BitCopyPrimitive (our value hook) ultimately fires per field.

**So the correct, path-agnostic hook is `+0x70` (WriteFields), arg3 = the field-index `CUtlVector<int>`:**
hook it, and for the issuer (RecipientCapture) + a hooked entity (`_currentEntityIndex`), **sorted-insert the
hooked field's index** into arg3 before calling the original (Trampoline). Hooking `+0x40` (CalcDelta) alone
would miss the change-tick path; hooking `+0x70` covers both because both converge there.

Hook via `IVirtualHook.Prepare(vtable, 14, fn)` (offset = slot INDEX, confirmed; `0x70/8 = 14`), where
`vtable = *(*(0x00ac4ae0))`. The field index space = the serializer's flattened-leaf index (the same int the
TO-snapshot change-list stores), so field-name→index = count leaves during the serializer record walk
(handles embedded fields like m_Glow.* by their flattened-leaf position).

### Spec completeness

All RE'd with high confidence: hook = vtable **slot 14 (`+0x70`)** of `*(0x00ac4ae0)` (path-agnostic
consumer); inject = sorted-insert into arg3's `CUtlVector<int>`; issuer+entity from existing captures;
field-name→flattened-leaf-index from the serializer walk. Build is mechanical. Remaining: confirm the
`+0x70` arg layout on Windows (same slot index) + the Windows global-load sig.
