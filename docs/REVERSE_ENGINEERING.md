# CS2 SendProxy — Reverse-Engineering Notes

How the CS2 (Source 2) networking path serializes per-field network values, and where SendProxy
intercepts it to substitute the value clients receive without changing real server-side state.

Binaries analyzed: CS2 dedicated server, Linux, stripped — `bin/linuxsteamrt64/libnetworksystem.so`
(serializer) and `libengine2.so` (send loop). Functions are located at runtime by byte-signature
(see the gamedata file); the RVAs quoted below are file-vaddr for the build documented here and are
provided for orientation only. Struct layouts are cross-checked against the
[hl2sdk `cs2` branch](https://github.com/alliedmodders/hl2sdk/tree/cs2).

All offsets and signatures are build-specific. Re-verify on engine updates; prefer the
signature/factory resolution in `.assets/gamedata/yappershq.sendproxy.jsonc` over any hardcoded value.

---

## 1. How CS2 differs from Source 1

| Source 1 (SourceMod SendProxy) | Source 2 / CS2 |
|---|---|
| `SendTable` tree of `SendProp`, each with a swappable `m_pProxyFn` the engine calls per-client during serialize | `CFlattenedSerializer` (schema-driven); per-field metadata in `CNetworkSerializerFieldInfo`; no per-instance proxy fn pointer |
| Engine packs **per client** → the proxy fires per (entity × client), so per-client values are free | Engine packs **once** into a shared `CFrameSnapshot` (`PackEntities`, threaded), then writes a per-client **delta** from that single buffer → encoded values are global; per-client controls *presence*, not value |
| `FindSendPropInfo(class, prop)` walks the SendTable | Entity exposes its serializer via a vtable call (§3) |

The consequence: a Source-1-style per-client value proxy has no direct analog. Uniform value
substitution hooks the encoder; per-client value substitution hooks the per-field bit-copy in the
send loop (§6).

---

## 2. The hook point — the encoder dispatch

In `CFlattenedSerializer::EncodeField` the per-field value write is:

```
(*(code*)**(void***)(fieldInfo + 0x38))(bf_write* buf, fieldInfo, void* valuePtr, void* ctx, uint unk)
```

The per-field encode fn is **vtable slot 0 of the encoder dispatch object at
`CNetworkSerializerFieldInfo + 0x38`**. Reconstructed signature:

```cpp
void EncodeFieldFn(bf_write* buf, CNetworkSerializerFieldInfo* field, void* valuePtr, void* ctx, uint32 unk);
```

`EncodeField` encodes a field **once** (no per-recipient loop); the recipients filter is consulted
elsewhere. `valuePtr` is the value's address in entity memory; in the encoder it arrives as `rcx`
(`*(int*)rcx`, §5). Hooking the encoder for one field swaps the value uniformly for all clients (§7).

---

## 3. Resolving a field from an entity

From hl2sdk-cs2 `public/entity2/entityinstance.h`:

```cpp
class CEntityInstance {
    virtual CNetworkSerializerClassInfo* GetNetworkSerializerInfo() = 0;  // vtable slot 0
};
```

So:

```
classInfo = entity->vtable[0](entity)        // GetNetworkSerializerInfo()
field     = classInfo->FindField("m_iHealth")
```

`CNetworkSerializerClassInfo` (`public/networksystem/inetworkserializer.h`) layout in order: leading
`ExcludeIncludeFilter_t` (2× `CUtlVector`), `CUtlStringToken m_nHash`, `CUtlString m_pszClassName`,
`CUtlVector<CNetworkSerializerFieldInfo*> m_Fields`, `CUtlHash m_FieldLookupTable`,
`int m_nTotalFieldEntries`. The inline `FindField(name)` resolves via `m_FieldLookupTable` →
`m_Fields[idx]`; fields match by `m_FieldNameHash` (`CUtlStringToken` = MurmurHash2 of the lowercased
prop name) or `m_pszFieldName`.

Useful handles: the global `IFlattenedSerializers` interface (`"FlattenedSerializersVersion001"`),
`CEntityClass.m_flattenedSerializer`, and ModSharp's `ISchemaManager.GetNetVarOffset(class, field)`
for value offsets.

### `CNetworkSerializerFieldInfo` — key members (SDK order)

`m_FieldNameHash` (`CUtlStringToken`), `m_pszFieldName`, `m_pszTypeName`, `m_pszRawType`,
`m_pszEncodedType`, `m_ClassNameHash`, `m_pszClassName`, `m_nFieldSize` (i32), `m_nFieldOffset`
(i32), `m_NetworkSerializer`, `m_NetworkEncoder`, `m_NetworkSendProxyRecipientsFilter`
(`shared_ptr<NetworkRecipientsFilter_t>`), `m_NetworkChangePointerCallback`, `m_NetworkBitCount`,
`m_NetworkEncodeFlags`. The runtime encoder dispatch ptr is at `+0x38`; the reliable field
identifier is the name `char*` at `+0x08` (see §4).

```cpp
struct NetworkRecipientsFilter_t {   // per-field, per-recipient send-proxy filter (presence only)
    void* m_unk001;
    void (*m_FilterFn)(CEntityInstance*, CCheckTransmitInfo*, CPlayerBitVec& player_mask);
    CUtlString m_FilterName;
};
```

---

## 4. Two distinct field structures

Two structures carry "field" data; keep them separate.

**(a) Flattened-serializer `fieldInfo`** — the argument the encoder receives. It is the full,
*flattened* field set, carries the encoder dispatch, and is what the substitution path reads.

- `fieldInfo + 0x08` = `char*` field name — the reliable identifier. It is **inheritance-proof**:
  `m_iHealth` appears here even though it is declared on `CBaseEntity`.
- `fieldInfo + 0x38` = encoder dispatch block (vtable slot 0 = the encoder fn).
- `fieldInfo + 0x28` = recipients filter (`m_NetworkSendProxyRecipientsFilter`) — gates per-client
  presence, never value.

**(b) Per-class `CNetworkSerializerClassInfo` records** — reached via entity `vtable[0]` (§3). A
leaner, *different* record: it holds only that class's **own** fields (e.g. 61 for `CCSPlayerPawn`),
**no inherited fields** (so `m_iHealth` is absent), name at `+0x08`, packed type/size at `+0x38`, and
**no encoder pointer**.

Resolve the encoder or any inherited field via the flattened `fieldInfo` (the encoder's argument),
not via the per-class records.

---

## 5. The per-field value encoder

`EncodeField` only *dispatches*. For an integer field the function that serializes the value is the
bucket-1 `default` encoder (`*(*(fieldInfo+0x38))`, vtable slot 0). Its body is canonical signed
zigzag then varint:

```
zz = (n << 1) ^ (n >> 63)      // signed zigzag
bf_write_varint(buf, zz)
```

ABI — five register args, no stack args (a trampoline detour is safe):

| reg | meaning |
|---|---|
| `rdi` | `bf_write*` (output bitstream) |
| `rsi` | `fieldInfo` |
| `rdx` | context (unused for the spoof) |
| `rcx` | **value pointer** — encoder reads `*(int*)rcx` |
| `r8d` | uint (flags/count) |

The encoder reads the value from `(%rcx)`, which is what makes a value swap a pointer redirect rather
than a memory write.

---

## 6. The encoder registry and gamedata-resolved classification

Field encoder-name → encoder fn is wired by a static descriptor table in `libnetworksystem.so`:

- **8 field-type buckets**, **0x10-byte** bucket records `{ handler*(+0x00), int count(+0x08) }`.
- Each bucket handler points at an array of encoder entries, **0x80-byte stride**: entry `+0x00` =
  encoder-name `char*`, entry `+0x30` = encode fn pointer.

The gamedata file declares the table base (`CFlattenedSerializer::EncoderRegistry`, byte-sig +
`+3 r` factory) and each per-bucket handler base as a standalone entry carrying the same registry
signature plus a full factory op-chain (`"factory": "+3 r +{b*16} d"` — `+3 r` RIP-resolves the table
base, `+{b*16}` offsets to bucket *b*'s slot, `d` dereferences the handler; ops defined in
`Engine/src/gamedata.cpp`). At install the library enumerates each
resolved bucket handler's entries once and maps `(bucket, encoder-name) → FieldType`, cross-checking
the bucket-1 `default` fn against the standalone `CFlattenedSerializer::EncodeInt32` signature. The
per-field hot path is then a single dictionary lookup of the live dispatch fn — no string scraping on
the send path, and no hardcoded table address.

> **Two fn locations, same pointer — don't confuse them.** A registry *entry* stores its encode fn at
> `entry + 0x30`; the map is built from that (`map[*(entry+0x30)] = type`). A *field's* live encoder is
> **slot 0** of its dispatch object: `*(*(fieldInfo + 0x38))` — deref `fieldInfo+0x38` to the dispatch
> object, read its first vtable slot. Classification reads the field's slot-0 fn and looks it up in the
> map; the two pointers are equal. Reading `*(*(fieldInfo+0x38)+0x30)` for a *field* is wrong (`+0x30`
> is the registry-entry offset) and classifies everything Unsupported — this was a real dev bug.
>
> Likewise the bucket-3 entry whose name is **`default`** IS the quantized-float encoder (single
> quantized floats like `m_flViewmodelFOV`); there is no entry literally named `"quantized"` or
> `"vector3"`. Matching those nonexistent names (instead of `default` + `qangle_pitch_yaw` +
> `qangle_precise`) leaves three b3 encoders unhooked — also a real dev bug.

Bucket semantics:

| Bucket | Field type | Encoding |
|---|---|---|
| b0 | (stub / unused) | no encoder entries; skipped |
| b1 | signed int (`default` varint, `fixed32`, `fixed64`) | signed zigzag varint |
| b2 | unsigned int | raw varint |
| b3 | qangle / vector / quantized-float family (`default`, `qangle`, `normal`, `coord`, `coord_integral`, `qangle_pitch_yaw`, `qangle_precise`) | 3× float32 or specialized |
| b4 | float32 | 32 raw bits |
| b5 | string | encoder reads `*valuePtr` as `char*`; emits `WriteString` 7-bit null-terminated |
| b6 | byte array | valuePtr struct: `+0x00` = `uint8* data`, `+0x28` = `uint32 count`; emits `varint(count)` then `count × 8` raw bits |
| b7 | bool | 1 bit |

Buckets b5 and b6 are supported for both uniform and per-client substitution. Bucket b0 has no
encoder entries and is never matched.

---

## 7. Uniform value substitution (all clients)

An `IDetourHook` on the relevant bucket encoder. In the hook:

```
1. name = *(char**)(fieldInfo + 0x08)        // rsi + 0x08
2. if name != target -> call trampoline unchanged
3. write the fake value into a stable native scratch (long-lived, not stack)
4. point rcx (the value pointer) at the scratch
5. call the trampoline
```

The encoder writes the fake into the bitstream; entity memory is never touched — a true
SourceMod-style send proxy where real server-side state stays real. Clients see the substituted
value uniformly.

---

## 8. The per-client send loop

| Function | Library | Role |
|---|---|---|
| `CNetworkGameServer::SendClientMessages` | engine2 | Pack the snapshot **once** (shared), then loop clients. |
| `CNetworkGameServer::PerClientEncode` | engine2 | Per-client encode entry. `CServerSideClient*` in `rsi` = the recipient. |
| `CNetworkGameServerBase::WriteDeltaEntity_Internal` | engine2 | Per-client, per-entity delta write. Parallel across worker threads. |
| `CFlattenedSerializer::WriteFieldList` | networksystem | The per-field copy loop. |

`SendClientMessages` packs all entities once into the shared `CFrameSnapshot` (encode happens here,
with `rsi=0` — no client), then writes each client's delta from that single buffer. The encoders are
**not** re-run per client, so a per-client *value* cannot be produced by re-running the encoder
without forcing a full per-client (or per-group) re-pack.

`PerClientEncode` is the per-client entry; the recipient `CServerSideClient*` arrives in `rsi`.
SendProxy detours it and stashes the recipient into a `[ThreadStatic]` (`RecipientCapture.cs`),
cleared on exit, so everything downstream on that worker thread can read it without threading the
client through every call frame.

`WriteDeltaEntity_Internal` is per-client and parallel (the same entity is written once per client
into distinct ctx / `bf_write` objects across worker threads). ctx (`rsi`): `+0x34` entIdx, `+0x88`
`bf_write`, `+0x90` from-snap, `+0x98` to-snap. Per-client value substitution rides this existing
per-client write — there is no N× re-pack. Thread-safety comes from per-call locals (never a shared
scratch); only the captured client is `[ThreadStatic]` and the serializer config is read-only.

---

## 9. Per-client value substitution (at the per-field copy)

The per-client send copies pre-encoded bits; it never invokes the encoder. But the per-field bit
**copy** runs once per client *and* knows the field identity, so a per-client value is injected by
substituting at the copy.

`WriteFieldList` uses a two-stream layout: field values are buffered into an intermediate `bf_write`
separately from the field-path stream, and the value buffer is appended at the end. Because the value
stream is decoupled from the path stream, a **variable-length** substitution (a varint whose byte
width differs from the original) is safe — it shifts only within the value buffer.

Per changed field the loop calls two functions back-to-back:

| Function | Library | Role |
|---|---|---|
| `GetBitRange` (`CFieldPath` resolver, `FUN_00426260`) | networksystem | Resolves the changed-field index → `[startBit,endBit)` and fills the `CFieldPath` for the field about to be copied. Fires immediately before each copy → the detour captures the path pointer. |
| `BitCopyPrimitive` (`FUN_00500b70`) | networksystem | The per-field copy primitive — the hook point. `byte copy(bf_write* dst, bf* src, uint bitcount)`; rdi=dst, rsi=src, rdx=bitcount. Reads `bitcount` bits from `src` at its cursor (`+0x10`) and **masked-overwrites** them into `dst` at its cursor (`+0x10`), advancing both. No field identity of its own — identity comes from the `GetBitRange` path plus the serializer captured on the `WriteFieldList` entry. |

`dst` is `WriteFieldList`'s stack-local intermediate value `bf_write`; its written-bit total is flushed
into the real per-client output at loop end using **its own cursor** (`+0x10`), so writing a different
bit-width than the original is fine as long as the cursor reflects it (varint/string are
self-delimiting; fixed-width types encode to the same width anyway).

**Substitution method — redirect the source, reuse the engine's own copy** (the approach that works;
the naive "call original, rewind dst cursor, re-emit over it" no-ops on the wire — don't use it):

1. Resolve the field name from the captured `CFieldPath` + serializer (§9a/§9b); look up the
   registration. If none → call the original copy unchanged.
2. Build the fake value pointer in the layout the field's encoder expects (same layout the uniform
   path uses — §RE_ENCODERS).
3. Encode the fake into a **fresh, zeroed, byte-aligned scratch `bf_write`** (cursor 0) by calling the
   field's own encoder (`*(*(fieldInfo+0x38))`, args rdi=&scratch, rsi=fieldInfo, rdx=paramsPtr,
   rcx=valuePtr). Byte-aligned cursor → the encoder takes its simple fast path. `N = scratch.cursor`
   (encoded bit width); bail to the original copy on overflow / `N == 0`.
4. Advance the real `src` cursor by `bitcount` (skip the real value so following fields still read
   correctly), rewind the scratch cursor to 0, and call the **original `BitCopyPrimitive(dst, scratch,
   N)`** — the same masked copy the engine uses for every field, now sourcing the fake bits.

This mirrors the uniform path exactly (the engine's own copy moves fake bits into the value buffer);
only the *source* of the fake bits differs (a per-client scratch built at copy time vs the shared
pre-encoded snapshot). Detail + decompile cites: `docs/RE_PERCLIENT_EMIT.md`.

Three detours cooperate: a `WriteFieldList`-entry shim stashes the serializer (`rdi`), `GetBitRange`
captures the field path, and `BitCopyPrimitive` performs the substitution — all driven off the
`[ThreadStatic]` recipient from `PerClientEncode`. All four short-circuit immediately when no
per-client registration exists (the send path is the hottest in the engine).

### 9a. The `CFieldPath` struct

`GetBitRange`'s **1st argument** (`pathOut`) is the `CFieldPath` it fills for the field being copied —
the detour captures this pointer and the resolver walks it (this is what the code does; reading the
3rd arg as a packed token was an earlier, abandoned approach). Layout:

- `+0x18` = level count (`short`), 1..3. Outside `[1,3]` ⇒ stale/garbage path → bail.
- `+0x1A` = flag (`byte`). If non-zero, the index array is out-of-line at `*(hdr)`; else the indices
  are inline starting at `hdr` itself.
- index array = `short` per level: `idxK = *(short*)(idxArr + k*2)`. `0x7FFF` = absent/sentinel (if at
  level 0 → bail; else stop descending).

Every dereference is bounds-checked (level count, and each index against the serializer's field-array
length, §9b) — a stale/garbage path must never deref off the array (that's the fatal-AV path; .NET
cannot catch an access violation, so the pointer/range guards are the only protection).

### 9b. The flattened-serializer field-array layout

To resolve a token to a leaf field name, walk the serializer's field array (distinct from the
per-class records of §4b):

- field-array element count = `*(int*)(serializer + 0x28)` (the `CUtlVector` size; bound every index
  against this before indexing — fall back to a hard cap if it reads implausibly).
- array base = `*(serializer + 0x30)` (deref once).
- records are **inline**, stride `0x2E`: `record_i = base + i*0x2E`.
- `record + 0x00` = `CNetworkSerializerFieldInfo*` (the leaf fieldInfo).
- `record + 0x08` = child `CFlattenedSerializer*` (descent into a nested level).
- `record + 0x2C` = data-region id (`byte`, used by the quantized live-value reconstruction, §9d).
- leaf name = `*(*(record + 0x00) + 0x08)` → `char*`.

Descent: at level 0, `rec = base + i0*0x2E`; if `i1 ≥ 0`, `child = *(rec+0x08)`,
`base = *(child+0x30)`, `rec = base + i1*0x2E`; same for `i2`. The leaf name is read off
`*(rec+0x00)+0x08`.

### 9c. Scope

Per-client value substitution is confirmed working live for top-level fields (e.g. spoofing
`CCSPlayerController::m_iPawnHealth` per recipient — the client decodes the substituted value). The
field-array walk (§9b) descends nested levels via the child serializer at `record+0x08`; single-level
nested fields resolve, but deeply nested sub-vectors addressed by **bare leaf name** can't be targeted
uniformly because names like `m_vecX` collide across `m_vecOrigin` / `m_vecViewOffset` / `m_vecVelocity`
— those need full-path (serializer + path) qualification, which is the per-client path's job, not the
name-keyed uniform path's.

---

## 10. Re-deriving signatures on a game update

All signatures live in `.assets/gamedata/yappershq.sendproxy.jsonc` and resolve at runtime via
`FindPattern`, so an engine update needs only the sigs regenerated — no code change.

**String anchors** — each target is reachable from a stable literal that exists on **both** platforms,
which is what makes a Windows sig (or a cross-platform `refs.strings`, §10a) tractable: find the string,
find the function that references it, makesig the prologue.

| Target | Anchor string (in/near the function) |
|---|---|
| `EncodeField` | `"CFlattenedSerializer::EncodeField encoder wrote %d bits"` |
| `WriteFieldList` | `"CFlattenedSerializer::WriteFieldList fieldDataBuf"` (debug name of its stack value buffer) |
| `GetBitRange` (`CFieldPath` resolver) | `"GetBitRange( %d -> %d ) end is before or same as start\n"`, file `"../public/networksystem/serializedentity.h"` |
| encoder registry | `"CNetworkSerializer: Unable to find network encoder named %s!"` (registry init / lookup) |
| `Encode` (caller) | `"Encode failure for entity %d"` |
| `EncodeInt32` | bucket-1 `default` entry fn (cross-checked against the registry walk) |
| `BitCopyPrimitive`, `SendClientMessages`, `PerClientEncode`, `WriteDeltaEntity_Internal` | no direct literal — resolved by prologue sig / call-site structure; on the Windows port, reach them from their **callers** (e.g. `WriteFieldList`→`BitCopyPrimitive`, `SendClientMessages`→`PerClientEncode`→`WriteDeltaEntity_Internal`) which do anchor. |

Regeneration: find the string's vaddr, find the instruction referencing it, walk back to the nearest
function prologue, emit the shortest unique prologue signature with nosoop's `makesig` (or the headless
Java port in `tools/`), which auto-wildcards address/relocation operands. Verify it matches exactly
once and points at a clean prologue.

The encoder registry table base is resolved from the table-load `lea` in the registry initializer
(`+3 r` factory) and the per-bucket handlers via the `+{b*16} d` factory chains (§6).

### 10a. Cross-platform `refs` (preferred over per-platform byte sigs)

ModSharp gamedata (`Engine/src/gamedata.cpp`) supports a `refs` block on any address entry, resolved
identically on Windows and Linux from a single definition — and resilient to recompiles that shift
byte patterns:

```jsonc
"CFlattenedSerializer::WriteFieldList": {
  "library": "networksystem",
  "linux":   "....",            // optional fallback / fast path
  "windows": "....",            // optional fallback / fast path
  "refs": {
    "strings": [ "CFlattenedSerializer::WriteFieldList fieldDataBuf" ],
    "vtables": [ "CSomeClass" ],   // resolve via a vtable by RTTI name
    "vtable":  "CSomeClass",       // or take the vtable itself
    "cvars":   [ "some_convar" ]   // resolve the fn that references a convar
  }
}
```

`refs.strings` resolves the function that references the literal — platform-agnostic where a unique
anchor exists. Every target with an anchor in the table above should carry a `refs.strings`; entries
without a clean anchor (`BitCopyPrimitive`, the registry `lea`) keep `windows` + `linux` byte sigs. The
current gamedata is **Linux byte-sig only** and uses **no refs** — adding `windows` sigs + `refs` is the
main task to make this cross-platform for a core merge.

---

### 9d. Live value reconstruction (quantized / coord / normal)

The quantized-family encoders (`default`/`coord`/`coord_integral`/`normal`) read a value *struct* (with
a component count at `+0x28` for the count-driven ones), and the quant logic needs the real components.
The per-client path reconstructs the live value pointer from the captured snapshot:

```
regionId   = *(byte*)  (record + 0x2C)
fieldOffset= *(ushort*)(fieldInfo + 0x20)
valuePtr   = *(nint*)(snapshot + 0x30 + regionId*8) + fieldOffset
```

where `snapshot` is `ctx + 0x90` (captured in the `WriteDeltaEntity_Internal` detour). Copy the live
struct into scratch, patch the float component(s) with the fake, then encode. (Uniform path: copy the
struct from the original `valuePtr` the hook receives and patch — preserves the `+0x28` count/lanes.)

---

## 11. Layout quick reference (the C++ port's offset table)

All struct offsets below are expected to be **platform-identical** (compiler-agnostic networked layout)
— verify the starred ones on Windows but they should match. Function addresses are build-specific
(gamedata-resolved). Calling convention differs: SysV `rdi,rsi,rdx,rcx,r8` (Linux) vs Win64
`rcx,rdx,r8,r9` — ModSharp's detour/trampoline maps it; the **argument order** is the same.

**`CNetworkSerializerFieldInfo` (flattened, leaf fieldInfo):**
| Offset | Meaning |
|---|---|
| `+0x08` | field name `char*` — inheritance-proof identifier |
| `+0x20` | field offset within the value region (`ushort`) — used by §9d |
| `+0x28` | recipients filter (presence only) |
| `+0x38` | encoder dispatch object ptr; **live encode fn = `*(*(fieldInfo+0x38))`** (slot 0) |
| `+0x40` | params base ptr; `paramsPtr = *(fieldInfo+0x40) + paramOff` |
| `+0xC9` | params byte offset (`byte`); `0xFF` ⇒ no params (`paramsPtr = 0`) |

**Encoder registry:** bucket record `{ handler*(+0x00), int count(+0x08) }`, stride `0x10`, 8 buckets.
Entry: name `char*` `+0x00`, encode fn `+0x30`, stride `0x80`.

**Encoder value-pointer conventions:** full per-encoder table in `docs/RE_ENCODERS.md`. Summary: int/uint
read `*(long/u64)`; float32 reads `*(double*)`; bool `*(byte*)`; string `*(char**)`; byte-array
`{+0x00 data*, +0x28 count}`; quantized/coord struct `{floats…, +0x28 count}`.

**`bf_write` / `bf_read`:**
| Offset | Meaning |
|---|---|
| `+0x00` | data ptr |
| `+0x08` | nDataBytes (`int`) |
| `+0x0C` | capacity bits (`int`) |
| `+0x10` | current cursor in bits (`int`) |
| `+0x20` | overflow flag (`byte`) |
| `+0x22` | a flag (`byte`); 0 = normal |

**`CFieldPath`** (`GetBitRange` arg0): level count `+0x18` (`short`, 1..3), out-of-line flag `+0x1A`
(`byte`), index shorts (out-of-line at `*(hdr)` if flag set, else inline at `hdr`), `0x7FFF` = absent.

**`CFlattenedSerializer`:** name `*(serializer+0x00)`, field-array count `*(int*)(serializer+0x28)`,
field-array base `*(serializer+0x30)`; inline records stride `0x2E` (`+0x00` leaf fieldInfo, `+0x08`
child serializer, `+0x2C` region id).

**`WriteDeltaEntity_Internal` ctx (`rsi`):** entIdx `+0x34`, `bf_write` `+0x88`, from-snap `+0x90`,
to-snap `+0x98`. Snapshot data regions at `snapshot + 0x30 + regionId*8`.

**Per-class field record** (entity vtable[0], distinct from flattened): own fields only, name `+0x08`,
type/size `+0x38`, no encoder.

---

## 12. Implementation map

- `Native/FieldSubstitution.cs` — the per-client engine: encoder classification (map build),
  `CFieldPath` struct walk + field-array resolve, the `WriteFieldList`/`GetBitRange`/`BitCopyPrimitive`/
  `WriteDeltaEntity_Internal` detours (all gated on registry emptiness), and the redirect-src emit.
- `Native/UniformEncoderHook.cs` — the uniform path: one shared `[UnmanagedCallersOnly]` hook over all
  bucket encoder fns, dispatch by the live fn (`*(*(fieldInfo+0x38))`) → trampoline + type, match by
  field name, redirect the value pointer (`rcx`) at a scratch built in that encoder's layout.
- `Native/RecipientCapture.cs` — `PerClientEncode` detour → `[ThreadStatic]` recipient.
- `SendProxyManager.cs` / `ISendProxyManager` — the public API; the core library ships no commands.
- `YappersHQ.SendProxy.Example` — drives the API through AdminManager-gated `sp_*` commands (split into
  `Commands/*` categories), and carries the read-only serializer probe (`SerializerProbe.cs`).

Engine addresses and encoder identities are resolved from
`.assets/gamedata/yappershq.sendproxy.jsonc`.

> **Port note (C++ .Core / C# .Shared):** the native engine above (`FieldSubstitution`,
> `UniformEncoderHook`, `RecipientCapture`) is the reference for the C++ side; `ISendProxyManager` + the
> delegates are the C# `.Shared` surface that survives. Before porting, settle the per-client value
> model so the C++ hot path doesn't call a managed delegate per field per client — prefer a
> native-readable value source (recipient mask + value), managed callback optional. See the pre-merge
> checklist in the project notes.
