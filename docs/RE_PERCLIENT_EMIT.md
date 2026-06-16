# Per-client emit RE — root-cause analysis (libnetworksystem.so)

Overnight RE session 2026-06-15. Goal: find why per-client field substitution fires
`SUBST-FAKE` (correct value + slot bits) but the client still sees the real value, while
the **uniform** path (encoder hook during shared pack) works.

All addresses are **file offsets**; Ghidra addr = file + 0x100000. Binary:
`libnetworksystem.so`. Decompiles captured in `cs2-bins/re-dumps/sp_noop_raw.log`
(script `ghidra_scripts/SPNoOpRE.java`).

## The per-client write path, end to end (decompile-cited)

`CFlattenedSerializer::WriteFieldList` = `FUN_00443b60` (file 0x343b60). Signature
(verified): `(this/serializer p1, mode p2, fieldDesc p3, bf_write* p4_out, descriptor* p5,
u32 p6, u32 p7, int* p8_changedList, u32 p9)`.

Key locals (verified from the decompile):

- **Intermediate value buffer** is a stack-local `bf_write`:
  - `puStack_18b38 = auStack_18038;` → data ptr (+0x00) = 98 KB stack buffer
  - `uStack_18b30 = 0xc000000018000;` → +0x08 nDataBytes = 0x18000 (98304), +0x0c nDataBits = 0xc0000 (786432)
  - `iStack_18b28 = 0;` → +0x10 cursor = 0
  - `uStack_18b18 = 0x100;` → +0x20 overflow byte = 0, +0x22 flag = 0
  - debug name `"CFlattenedSerializer::WriteFieldList fieldDataBuf"`
- **Source** bf (the shared pre-encoded snapshot bits): `lStack_18b08 = param_5[3];` (a bf_read; cursor @+0x10).
- **Two output streams:** field-path **indices** are written directly into the real output
  `param_4` via a vtable encode call; field **values** go into the stack-local intermediate.

### Per-field loop (one field per copy — NOT coalesced)

At `LAB_004443e0`:
```c
FUN_00426260(appppsStack_18b58, DAT_00577a58, uVar24);     // resolve CFieldPath for field token uVar24
lVar33 = (long)iStack_18bb8 + 1;
iVar30 = *(int *)(param_5[1] + lVar33 * 4);                 // bitTable[idx+1] = start bit
if (iStack_18bb8 + 1 < (int)param_5[6])
    iVar3 = *(int *)(param_5[1] + 4 + lVar33 * 4);          // bitTable[idx+2] = end bit
else
    iVar3 = *(int *)((long)param_5 + 0x34);                 // total bits (last field)
...
FUN_00500b70(&puStack_18b38, &lStack_18b08, iVar3 - iVar30);  // BitCopyPrimitive(dst=intermediate, src=snapshot, bitcount=field width)
```

- `bitcount = bitTable[idx+2] - bitTable[idx+1]` = **exactly one field's** encoded width.
- `iStack_18bb8` (bit-range index) and `uVar24` (path token → field name) are both derived
  from the same `puVar18` position (`iStack_18bb8 = (puVar18 - (fieldArr+8)) >> 2`), so the
  field we **name** is the field whose **bit range** we copy. No desync.

### The flush (uses the intermediate's own cursor)

After the loop:
```c
iVar30 = iStack_18b28;                                       // intermediate cursor (bits written)
...
FUN_005064c0(param_4, auStack_18038, iVar30);                // WriteBits: copy iVar30 bits → real output
```
**The flush length is the intermediate buffer's own write cursor (+0x10)** — not a table
total. ⇒ writing more/fewer bits into the intermediate is fine *as long as the cursor
reflects it*. (`FUN_005064c0` = `WriteBits`, file 0x4064c0.)

### `bVar9` re-encode branch

Set true only if iterating `param_3`'s field-change-tracker list finds a registered proxy
(`(*(code**)(*(long*)*puVar39 + 0x20))(...) != 0`). On a normal server with no such
trackers it is **false**, so the large re-encode block after the copy is skipped and does
**not** overwrite our value. Not a factor in normal operation.

## The primitives

### `BitCopyPrimitive` = `FUN_00500b70` (file 0x400b70)
`byte BitCopyPrimitive(bf_write* dst, bf* src, uint bitcount)`. Reads `bitcount` bits from
`src` starting at `src`+0x10 cursor (advancing it) and **masked-overwrites** them into `dst`
at `dst`+0x10 cursor (advancing it). Returns `!overflow`. bf fields used: data @+0x00,
nDataBytes @+0x08, nDataBits @+0x0c, cursor @+0x10, overflow @+0x20, flag @+0x22.

### uint encoder = `FUN_004c8e90` (file 0x3c8e90)
```c
undefined8 FUN_004c8e90(bf_write* p1, _ p2, _ p3, ulong* p4_valuePtr) {
    FUN_00500890(p1, *p4_valuePtr);   // varint_writer(bf_write, value)
    return 1;
}
```
Ignores p2 (fieldInfo) and p3 (params). Reads `*valuePtr`, writes self-delimiting varint.

### varint writer = `FUN_00500890` (file 0x400890)
```c
uVar8 = *(uint*)(p1+0x10);            // cursor
uVar2 = *(uint*)(p1+0x0c);            // capacity bits
if (((uVar8 & 7) != 0) || (cap <= cursor+0x4f)) {
    // SLOW bit-by-bit path: handles UNALIGNED cursor via masked writes — verified correct
} else {
    // FAST byte-aligned path: writes whole bytes at *(p1) + cursor/8
}
*(int*)(p1+0x10) += iVar10;           // advance cursor by encoded bit width
```
**Important verified fact:** the slow path correctly handles a non-byte-aligned write
cursor. So calling the encoder at a mid-stream (unaligned) cursor is *not* itself broken —
that earlier hypothesis is wrong.

## Why the direct-encoder-call emit no-ops — and the fix

The previous emit did:
```
*(int*)(src + 0x10) += bitcount;                    // skip real value in source
encoderFn(dst, fieldInfo, paramsPtr, valuePtr, 0);  // write fake straight into intermediate at its cursor
return 1;
```
Every individual mechanic above checks out for this, so the static decompile does not
explain the wire no-op by itself. Rather than guess at the runtime cause, the fix uses the
mechanism that is **already proven to work** — the uniform path:

> Uniform substitution works because the engine's own `BitCopyPrimitive` copies **fake
> source bits** into the value buffer (the encoder was hooked at pack time, so the shared
> snapshot already holds fake bits). The copy into the value buffer is unchanged engine code.

The per-client fix reproduces exactly that, differing only in *when/where* the fake bits are
produced:

1. Build a fresh, zeroed, **byte-aligned** scratch `bf_write` (cursor 0).
2. Run the field's own encoder into the scratch → fake's pre-encoded bits at scratch[0..N],
   `N` = scratch cursor (the encoded bit width). Byte-aligned ⇒ encoder takes its simple
   fast path.
3. Advance the real `src` cursor by `bitcount` (skip the real value so following fields read
   correctly).
4. Rewind scratch cursor to 0 and call the **original `BitCopyPrimitive(dst, scratch, N)`** —
   the same proven masked copy the engine uses for every field, now sourcing our fake bits.

Correctness for all field families:
- **Fixed-width** (float32=32, bool=1, quantized=params): encoder emits exactly the fixed
  width ⇒ `N == bitcount`.
- **Variable-width** (varint int/uint, length-prefixed string/bytes): self-delimiting ⇒ the
  client decoder consumes exactly `N` bits and lands on the next field even if `N != bitcount`.

Because the value-buffer flush uses the intermediate's own cursor, advancing `dst` by `N`
(via the original copy) keeps the flush length correct automatically.

This is strictly more robust than the direct call: the only non-engine step is producing
fake bits in a private scratch buffer; the write into the live value buffer is 100% the
engine's own `BitCopyPrimitive`, identical to the working uniform path.

## What was implemented (not yet deployed)

`FieldSubstitution.ValueCopyHook` emit (src/YappersHQ.SendProxy/Native/FieldSubstitution.cs):

1. The `valuePtr`-building switch is **unchanged** — and is byte-for-byte the same layout the
   *working* `UniformEncoderHook.TryBuildScratch` uses (verified: both store `*(double*)` for
   float, `*(ulong*)` for uint, `{data,+0x28 count}` for byte-arrays, etc.). So the encoder
   reads the fake exactly as it does on the uniform path.
2. New emit: build a zeroed, byte-aligned scratch `bf_write` (header `ScratchBfSize=0x40`,
   data buffer sized `encBound` per field family, bounded by `MaxSubstituteBytes`), run the
   field's own encoder into it, read `encodedBits` from scratch+0x10, bail on overflow/zero,
   advance the real `src` cursor by `bitcount`, rewind scratch to 0, and
   `CallOriginal(dst, scratch, encodedBits)`.
3. Two capped diagnostics for the one confirming deploy: `SUBST-EMIT` (encodedBits, bitcount,
   first two scratch bytes, dst cursor before) and `SUBST-EMIT-DONE` (copyOk, dst cursor
   after). If the dst cursor advances by `encodedBits`, the fake bits landed in the value
   buffer.

## SendFake (one-shot push) — mechanism, verified

Goal: push a fake to a single client *now*, without a persistent hook, then optionally `Hook`.

- **Force re-transmit:** `IBaseEntity.NetworkStateChanged(field)` (→ `ISchemaObject`)
  routes to `SchemaSystem.NetVarStateChanged` →
  `Entity.SetStateChanged(ptr, schemaField.Offset+extraOffset)` (or `NetworkStateChanged` for
  a chained class) — verified in `Sharp.Core/Helpers/SchemaSystem.cs`. This sets the engine's
  **per-field dirty bit**, which drives the entity into the next snapshot's change-list. The
  resend is gated on the dirty bit, **not** a value diff, so it fires even if the real value is
  unchanged — i.e. *not coalesced away*.
- **Carry the fake once:** a one-shot entity-scoped registration bound to the target
  `CServerSideClient*` (`IGameClient.GetAbsPtr()`, matched against
  `RecipientCapture.CurrentClient`). In `ValueCopyHook`, non-target clients pass through; the
  target client gets the fake via the same redirect-src emit and the registration self-removes.

`SendFake` therefore rides the exact same emit path as per-client `Hook` — confirming that
path is the single thing the morning deploy must validate.

> **Dependency note:** entity-scoped registrations (entity `Hook` and `SendFake`) match on the
> runtime entity index from `WriteDeltaEntity` ctx+0x34 (`WdeEntityCaptureHook`). If that
> capture is unreliable, the ent-scoped lookup misses → passthrough. The all-entities `Hook`
> path does not depend on it. Check the `SUBST-EMIT` log shows the expected `ent` on test.

## Morning test plan (one deploy)

1. Deploy build to `ttt`, rewrite + readback-verify gamedata (`yappershq.sendproxy.jsonc`,
   buckets 1–7 present), delete any nested `gamedata/gamedata/`, restart. (Gamedata was **not**
   changed this session — same file as before.)
2. `sp_setpc CCSPlayerController m_iPawnHealth int`, then `slap @me 1`. Watch logs:
   - `SUBST-EMIT … encodedBits=N bitcount=8 scratch=[XX YY] dstCursorBefore=…`
   - `SUBST-EMIT-DONE … copyOk=… dstCursorAfter=…`  ⇒ `dstCursorAfter - dstCursorBefore == N`.
   - If the cursor advanced by N and the HUD shows the fake (1–64), per-client works.
3. `sp_sendfake 1337` — HUD should show 1337 once on the next update (one-shot), then revert.
4. If the cursor advances but the HUD still shows real: the fake bits *are* in the value
   buffer, so the gap is upstream (path-stream/decoder) — capture a fresh `SUBST-EMIT` set and
   re-RE `FUN_005064c0` (the flush) vs the client decoder.
5. Once confirmed: strip `PCDIAG`, `SUBST-FAKE`, `SUBST-EMIT*`, the boot encoder-map diag, and
   `LogDiscoveredField`.
