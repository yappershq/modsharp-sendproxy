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
`+3 r` factory) and derives each per-bucket handler base from it declaratively with the factory
op-chain (`"base": "…EncoderRegistry", "linux": { "factory": "+{b*16} d" }` — `+N` offset then `d`
dereference; ops defined in `Engine/src/gamedata.cpp`). At install the library enumerates each
resolved bucket handler's entries once and maps `(bucket, encoder-name) → FieldType`, cross-checking
the bucket-1 `default` fn against the standalone `CFlattenedSerializer::EncodeInt32` signature. The
per-field hot path is then a single dictionary lookup of the live dispatch fn
(`*(*(fieldInfo+0x38)+0x30)`) — no string scraping on the send path, and no hardcoded table address.

Bucket semantics: **b1** signed int (`default` varint, `fixed32`, `fixed64`), **b2** unsigned int,
**b3** qangle / vector / quantized-float family (`default`, `qangle`, `normal`, `coord`,
`coord_integral`, `qangle_pitch_yaw`, `qangle_precise`), **b4** float32, **b7** bool.

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
| `GetBitRange` | networksystem | Resolves the changed-field index → `[startBit,endBit)`. **3rd arg = the field token** (§9a). Fires immediately before each copy → the detour captures the token. |
| `BitCopyPrimitive` | networksystem | The per-field copy primitive — the hook point. `byte copy(bf_write* dst, bf_read* src, uint bitcount)`; rdi=dst, rsi=src, rdx=bitcount. The `bf_write` bit cursor is at `*(int*)(dst + 0x10)`. A pure bulk copy with no field identity of its own — identity comes from `GetBitRange`'s token plus the serializer captured on the `WriteFieldList` entry. |
| `VarintWriter` | networksystem | The varint primitive used to emit the substitute. `void write(bf_write* dst, uint32 zigzag)`; rdi=dst, rsi=zigzag. Self-zigzag: `zigzag = (uint)((v<<1)^(v>>31))`. |

Substitution sequence (in the `BitCopyPrimitive` detour):

1. Save the dst cursor: `savedCursor = *(int*)(dst + 0x10)`.
2. Call the original copy (advances src and dst, writes the real bits).
3. Rewind: `*(int*)(dst + 0x10) = savedCursor`.
4. Emit the substitute (`VarintWriter`, or the appropriate bucket encoder for non-int types),
   overwriting the just-copied bits from the saved cursor.

Three detours cooperate: a `WriteFieldList`-entry shim stashes the serializer (`rdi`), `GetBitRange`
captures the field token, and `BitCopyPrimitive` performs the substitution — all driven off the
`[ThreadStatic]` recipient from `PerClientEncode`.

### 9a. The `CFieldPath` token

`GetBitRange`'s 3rd argument is a packed `CFieldPath` — a 3-level nested path with a −1 bias per level
and an `0x7FF` mask:

```
token = ((i0+1) << 22) | ((i1+1) << 11) | (i2+1)        // pack
i0 = ((token >> 22) & 0x7FF) - 1                          // decode
i1 = ((token >> 11) & 0x7FF) - 1
i2 = ( token        & 0x7FF) - 1                          // -1 ⇒ level absent
```

`0x02810008` → `[9, 31, 7]`; `0x01400000` → `[4]` (i1 = i2 = −1). `0xFFFFFFFF` is the root sentinel
(skip); `0` is "all absent" (skip).

### 9b. The flattened-serializer field-array layout

To resolve a token to a leaf field name, walk the serializer's field array (distinct from the
per-class records of §4b):

- array base = `*(serializer + 0x30)` (deref once).
- records are **inline**, stride `0x2E`: `record_i = base + i*0x2E`.
- `record + 0x00` = `CNetworkSerializerFieldInfo*` (the leaf fieldInfo).
- `record + 0x08` = child `CFlattenedSerializer*` (descent into a nested level).
- leaf name = `*(*(record + 0x00) + 0x08)` → `char*`.

Descent: at level 0, `rec = base + i0*0x2E`; if `i1 ≥ 0`, `child = *(rec+0x08)`,
`base = *(child+0x30)`, `rec = base + i1*0x2E`; same for `i2`. The leaf name is read off
`*(rec+0x00)+0x08`.

### 9c. Scope

Top-level fields resolve cleanly (`m_iHealth`, `m_ArmorValue`, `m_nTickBase`) and are the supported
substitution targets. Nested-path descent (`i1`/`i2 ≥ 0`) is not yet reliable for every field; deeply
nested fields are out of scope until the child-serializer descent offsets are fully mapped.

---

## 10. Re-deriving signatures on a game update

All signatures live in `.assets/gamedata/yappershq.sendproxy.jsonc` and resolve at runtime via
`FindPattern`, so an engine update needs only the sigs regenerated — no code change.

`EncodeField` carries the anchor string `"CFlattenedSerializer::EncodeField encoder wrote %d bits"`.
To regenerate its sig: find the string's vaddr (`readelf -p .rodata`), find the instruction
referencing it (`objdump -d | grep -F '# <vaddr>'`), walk back to the nearest function prologue
(`push %rbp; mov %rsp,%rbp; push r15..r12; push rbx; sub rsp,…`), and emit the shortest unique
prologue signature with nosoop's `makesig` (or the headless Java port in `tools/`), which
auto-wildcards address/relocation operands. Verify the sig matches exactly once and points at a clean
prologue.

The encoder registry table base is resolved from the table-load `lea` in the registry initializer
(`+3 r` factory) and the per-bucket handlers via the `+{b*16} d` factory chains (§6) — no string
anchor needed.

---

## 11. Layout quick reference

| Thing | Value |
|---|---|
| `CEntityInstance` vtable slot → `GetNetworkSerializerInfo()` | 0 |
| `CNetworkSerializerFieldInfo` encoder dispatch ptr | `+0x38` (encode fn = its vtable slot 0) |
| flattened `fieldInfo + 0x08` | field name (`char*`) — inheritance-proof identifier |
| `fieldInfo + 0x28` | recipients filter (presence only) |
| encoder registry bucket record | `{ handler*(+0x00), count(+0x08) }`, stride `0x10` |
| encoder entry | name `char*` `+0x00`, encode fn `+0x30`, stride `0x80` |
| per-class field record (entity vtable[0]) | own fields only, name `+0x08`, type/size `+0x38`, no encoder |
| `WriteDeltaEntity_Internal` ctx (`rsi`) | entIdx `+0x34`, `bf_write` `+0x88`, from-snap `+0x90`, to-snap `+0x98` |
| `bf_write` bit cursor | `*(int*)(bf_write + 0x10)` |
| serializer field-array base | `*(serializer + 0x30)`, inline records stride `0x2E` |

Function addresses are build-specific and resolved at runtime from the gamedata file.

---

## 12. Implementation map

- `Native/FieldSubstitution.cs` — the substitution engine (encoder classification, `CFieldPath`
  decode, field-array walk, uniform and per-client substitution).
- `Native/RecipientCapture.cs` — `PerClientEncode` detour → `[ThreadStatic]` recipient.
- `SendProxyManager.cs` / `ISendProxyManager` — the public API; the core library ships no commands.
- `YappersHQ.SendProxy.Example` — drives the API through AdminManager-gated `sp_example_*` commands,
  and carries the read-only serializer probe (`SerializerProbe.cs`) used during this work.

Engine addresses and encoder identities are resolved from
`.assets/gamedata/yappershq.sendproxy.jsonc`.
