# CS2 SendProxy — Reverse-Engineering Notes

Everything learned reverse-engineering the CS2 (Source 2) networking path to port SourceMod's
SendProxy. Goal: intercept per-field network serialization and substitute the value sent to
clients, without changing real server-side state.

Binaries analyzed (CS2 dedicated server, Linux, build **2026-06-02**, stripped):
`bin/linuxsteamrt64/libnetworksystem.so` (serializer), `libengine2.so` (send loop). Symbols are
stripped (only std/protobuf templates leak via dynsym), so functions were located by the **log
strings they reference** + RTTI class names, in Ghidra. Struct layouts cross-checked against the
[hl2sdk `cs2` branch](https://github.com/alliedmodders/hl2sdk/tree/cs2).

> ⚠️ All offsets/slots are **build-specific** — re-verify on engine updates. Prefer the
> game-function resolution (below) over hardcoded offsets wherever possible.

---

## 1. How CS2 differs from Source 1 (why a naive port fails)

| Source 1 (SourceMod SendProxy) | Source 2 / CS2 |
|---|---|
| `SendTable` tree of `SendProp`, each with a swappable `m_pProxyFn` the engine calls per-client during serialize | **`CFlattenedSerializer`** (schema-driven); per-field metadata in `CNetworkSerializerFieldInfo`; **no per-instance proxy fn pointer** |
| Engine packs **per client** (`SV_ComputeClientPacks` → `SendTable_Encode` each client) → proxy fires per (entity × client) → per-client values are free | Engine **packs once** into a shared `CFrameSnapshot` (`CNetworkGameServer::PackEntities`, threaded), then per-client **delta** (`WriteDeltaEntity_Internal`) gated by a recipients **bitmask** → values are global; per-client = presence, not value |
| `gamehelpers->FindSendPropInfo(class, prop)` walks the SendTable | Entity exposes its serializer via a **vtable call** (below) |

Community confirmation (ModSharp #programming): "in cs2 only one already-packed shared data set
is referenced at transmission, very difficult to reproduce [per-client]"; "s2 uses schema system."

---

## 2. The hook point — the `m_pProxyFn` analog

In **`CFlattenedSerializer::EncodeField`** (libnetworksystem.so @ `0x4334e0`, anchor string
`"CFlattenedSerializer::EncodeField encoder wrote %d bits %s %s %s!"`), the per-field value write is:

```
(*(code*)**(void***)(fieldInfo + 0x38))(bf_write* buf, fieldInfo, void* valuePtr, void* ctx, uint unk)
```

So the per-field encode fn = **vtable slot 0 of the encoder dispatch object at `CNetworkSerializerFieldInfo + 0x38`**.
- `valuePtr` = the value's address in entity memory. ⚠️ **CORRECTION:** an earlier note read this off
  `field+0x40` — that is wrong (`+0x40` reads `0` in practice; it is NOT `m_nFieldOffset`). See §11.
  In the working detour the value pointer arrives as the encoder's `rcx` arg (`*(int*)rcx`, §9/§12).
- Reconstructed signature:
  `void EncodeFieldFn(bf_write* buf, CNetworkSerializerFieldInfo* field, void* valuePtr, void* ctx, uint32 unk)`.

**Swap that pointer (per hooked field only)** → our fn reads `valuePtr`, runs the callback, calls
the original with the modified value. Untouched fields keep their native pointer → zero cost for the
other ~10k entities. `EncodeField` encodes ONCE (no per-recipient loop); the recipients filter is
consulted elsewhere (`GatherSendProxyResults_R`).

---

## 3. Game-function resolution (preferred — robust, no hardcoded addresses)

Found in hl2sdk-cs2 `public/entity2/entityinstance.h`:

```cpp
class CEntityInstance {
    virtual CNetworkSerializerClassInfo* GetNetworkSerializerInfo() = 0;  // VTABLE SLOT 0
    ...
};
```

So from any entity:
```
classInfo = entity->vtable[0](entity)   // GetNetworkSerializerInfo()
field     = classInfo->FindField("m_iHealth")
swap field->[+0x38] encoder dispatch slot 0
```

`CNetworkSerializerClassInfo` (`public/networksystem/inetworkserializer.h`) layout (order):
leading `ExcludeIncludeFilter_t` (2× `CUtlVector`), `CUtlStringToken m_nHash`,
`CUtlString m_pszClassName`, **`CUtlVector<CNetworkSerializerFieldInfo*> m_Fields`**,
`CUtlHash m_FieldLookupTable`, `int m_nTotalFieldEntries`, ... It has a non-virtual inline
`FindField(const char* name)` (uses `m_FieldLookupTable.Find` → `m_Fields[idx]`). Match fields by
`m_FieldNameHash` (CUtlStringToken = MurmurHash2 of the **lowercased** prop name) or `m_pszFieldName`.

Other useful handles:
- Global interface **`IFlattenedSerializers`** (`"FlattenedSerializersVersion001"`, `g_pFlattenedSerializers`) — resolvable via `ILibraryModule.FindInterface`.
- `CEntityClass.m_flattenedSerializer` (`FlattenedSerializerDesc_t`).
- ModSharp: `INativeObject.GetAbsPtr()` → native ptr; `ISchemaManager.GetNetVarOffset(class, field)` → value offset.

### `CNetworkSerializerFieldInfo` (the SendProp analog) — key members (SDK order)
`m_FieldNameHash`(CUtlStringToken), `m_pszFieldName`, `m_pszTypeName`, `m_pszRawType`,
`m_pszEncodedType`, `m_ClassNameHash`, `m_pszClassName`, `m_nFieldSize`(i32), `m_nFieldOffset`(i32),
... `m_NetworkSerializer`, `m_NetworkEncoder`, `m_NetworkSendProxyRecipientsFilter`
(`shared_ptr<NetworkRecipientsFilter_t>`), `m_NetworkChangePointerCallback`, ... `m_NetworkBitCount`,
`m_NetworkEncodeFlags`. The runtime encoder dispatch ptr is at **`+0x38`**.

```cpp
struct NetworkRecipientsFilter_t {                       // per-field, per-recipient send-proxy filter
    void* m_unk001;
    void (*m_FilterFn)(CEntityInstance*, CCheckTransmitInfo*, CPlayerBitVec& player_mask);
    CUtlString m_FilterName;
};
```

---

## 4. The per-client send loop (Phase 2)

`libengine2.so`:
- **`CNetworkGameServer::SendClientMessages`** @ `0x7a7280` (anchor `"SV:  SendClientMessages"`):
  one shared entity pack (`PackEntities`, threaded), THEN loops clients (array at `gameServer+0x4b`,
  count `+0x4a`) calling per-client `SendSnapshot` at **vtable +0x80** (current client in scope).
- **`CNetworkGameServerBase::WriteDeltaEntity_Internal`** @ `0x7d15c0` (anchor
  `"SV: CNetworkGameServerBase::WriteDeltaEntity_Internal merging changes added in %d additional fields!"`):
  re-encodes field values from the snapshot (CalcDelta → WriteFields), but in the SHARED phase, **no
  client param**. Threaded pack → any "current client" stash must be `thread_local`.

**Per-client value verdict:** uniform spoof = the `+0x38` swap (§2). Per-client *presence* = the
recipients filter. Per-client *different value* (full matrix) = set a `thread_local` current-client at
the per-client send (+0x80) and override only hooked entities' hooked fields in that client's stream
(cost = hooked × clients). Avoid forcing a full per-client re-pack (`pack × N` — kills perf at 64p/10k).

---

## 4b. Re-deriving the EncodeField signature (do this on every game update)

The `EncodeField` byte-signature is build-specific and **will break on engine updates**. To regenerate:

1. `strings libnetworksystem.so | grep 'CFlattenedSerializer::EncodeField encoder wrote'` — confirm the
   anchor string still exists.
2. In Ghidra (or via the string xref), find the function that references it. ⚠️ Ghidra frequently sets
   the function entry **mid-instruction** on this stripped binary (it put EncodeField's entry 0x10 bytes
   too far in, at 0x4334e0 instead of 0x4334d0). **Always verify the real entry**: disassemble backward
   until you hit a clean prologue right after a prior `ret`:
   `55 (push rbp) / 48 89 E5 (mov rbp,rsp) / 41 57 41 56 41 55 41 54 (push r15..r12) / 53 (push rbx) / 48 83 EC xx (sub rsp,xx)`.
3. The function is identifiable a few bytes in by its **31-bit hash mask + loop-bound check**:
   `8B 57 04 (mov edx,[rdi+4]) / 89 D3 (mov ebx,edx) / 81 E3 FF FF FF 7F (and ebx,0x7fffffff) / 39 F3 (cmp ebx,esi) / 0F 8D (jge)`.
   That `81 E3 FF FF FF 7F` is the most distinctive marker — include it so the sig is unique.
4. Build the sig from the prologue through the hash mask. Current (build 2026-06-02):
   `55 48 89 E5 41 57 41 56 41 55 41 54 53 48 83 EC 08 8B 57 04 89 D3 81 E3 FF FF FF 7F`
   Wildcard (`?`) any rel32/rip-relative displacement bytes — there are none in this prologue.
5. Verify at runtime: `NetworkSystem.FindPattern(sig)` must return a non-zero address pointing at the
   prologue (`55 48 89 E5 …`). The module logs this on load (`SendProxy resolve EncodeField (sig)`).
6. ⚠️ The function's true **argument signature** must be re-derived from the *correct* entry (0x4334d0),
   not Ghidra's mid-function entry — decompiling the wrong entry gives a wrong arg layout, and a detour
   built on it will crash. Confirm arity/types before installing the detour.

## 4c. Remote GDB workflow (pterodactyl GDB_DEBUG_PORT → gdbserver) — WORKS

The server can be launched under `gdbserver --no-disable-randomization :PORT` (waits for a
debugger to connect+continue before starting). Pitfalls + the working recipe:

- **Single client only.** gdbserver allows ONE debugger. If IDA is attached, a second gdb gets
  `unrecognized item "timeout" in "qSupported"` + `Remote replied unexpectedly to 'vMustReplyEmpty': timeout`.
  Use gdb OR IDA, not both.
- **Kill the slow remote symbol reads** (these froze the server for minutes): set sysroot +
  debug-file-directory to an empty dir so gdb can't fetch any `.so`/debug over the link:
  ```
  set sysroot /tmp/empty
  set debug-file-directory /tmp/empty
  set auto-solib-add off
  set solib-search-path /tmp/empty
  set remotetimeout 180
  target remote <ip>:<port>
  ```
- **`catch load <lib>` is unreliable** here (didn't stop on the lib). Instead just `continue` to
  boot, then **SIGINT the gdb process** (`kill -INT <gdbpid>`) to break into the running server.
- **Get lib bases** from `info proc mappings` (lowest mapping of each `.so`). Then break by
  `base + RVA` (e.g. EncodeField = libnetworksystem base + 0x4334d0 — verified: prologue
  `55 48 89 e5 41 57 41 56 ...`).
- **Encoders only fire for REAL network clients** — bots have no netchan, so they don't trigger
  the per-client snapshot encode. Need a real player connected to capture `SendClientMessages`/
  the field encoder hits.
- Drive it non-interactively with a gdb Python script (`/home/claude/cs2-bins/sp_gdb3.py`):
  log-only `gdb.Breakpoint` subclasses (`stop()` returns False → log regs + auto-resume, never
  freeze). The game client connection is independent of the gdb port, so a player can join the
  game while gdb stays attached.

## 5. ModSharp resolution tooling (verified live on a server)

- `ILibraryModule.FindString(str)` → `ILibraryModule.FindFunction(nint ptr)` reliably locates a
  function by a string it references. **Verified:** resolved `SendClientMessages` to the exact
  address matching Ghidra. Use the §2/§4 anchor strings.
- ⚠️ `FindFunction(string)` resolves by **symbol name** (throws on stripped binaries) — do NOT use
  it for string anchors.
- ⚠️ `FindString` returned 0 for the long format-string anchors (likely a length cap) while short
  anchors worked — prefer short unique anchors, or `FindInterface`/vtable resolution.
- `GameData.Register("name")` loads `gamedata/name.jsonc` (NOT `.games.jsonc`) and **throws** when
  missing — wrap it.

---

## 6. Dead ends (don't repeat)

- Serializer-by-name registry lookup (xref'd `~0x4901a6` from `"unable to find serializer named %s"`):
  Ghidra auto-analysis left it undefined; `0x4901a6` isn't in a defined function; the string ref is
  RIP-relative (not an operand scalar). **Superseded** — use the entity vtable[0] game function (§3)
  instead of hunting the registry.
- Raw byte-signature extraction at function entries via `objdump --start-address` came out
  mid-instruction; not reliable without careful prologue masking. Prefer string-anchor/vtable resolution.

---

## 7. Offsets / slots quick table

| Thing | Value | Confidence |
|---|---|---|
| `CEntityInstance` vtable slot → `GetNetworkSerializerInfo()` | **0** | SDK |
| `CNetworkSerializerFieldInfo` encoder dispatch ptr | **+0x38** | RE (EncodeField) |
| └ encode fn | vtable slot 0 of `*(field+0x38)` | RE |
| flattened `fieldInfo + 0x08` = **field name (`char*`)** | the reliable, inheritance-proof id | RE (encoder hook, **see §11**) |
| `fieldInfo + 0x28` = recipients filter (`m_NetworkSendProxyRecipientsFilter`) | per-client *presence* | RE |
| ⚠️ `fieldInfo + 0x40` — **NOT `m_nFieldOffset`** (reads **0**; use name @ +0x08) | corrected; old "value offset" claim was wrong | RE (**see §11**) |
| └ secondary adjust byte | **+0xC9** (0xFF = none) | RE |
| `CNetworkSerializerClassInfo` → `m_Fields` (CUtlVector) | after filter+hash+classname | **confirm via dump** |
| per-class field record (via entity vtable[0]) | OWN fields only, name @ +0x08, type/size @ +0x38, NO encoder | RE (**see §11**) |
| `CFlattenedSerializer` name / count / field-array | +0x00 / +0x08 / +0x10 (ptr array, stride 8) | RE (WriteFieldList) |
| **int32 value encoder** (zigzag + varint; Phase-1 detour target) | networksystem `0x3c8e70` | RE (**see §9**) |
| └ bf_write varint writer | networksystem `0x400890` | RE |
| **encoder registry table** (8 buckets × 0x80 stride; name @ slot0, fn @ slot6) | networksystem `0x45f360` | RE (**see §10**) |
| **InitFakeField** (`FUN_0044b980`; copies slots [6,7,1,4,5] → fieldInfo+0x38) | networksystem `0x34b980` | RE (**see §10**) |
| `SendClientMessages` | engine2 `0x7a7280` | RE |
| └ per-client SendSnapshot | vtable +0x80 | RE |
| `WriteDeltaEntity_Internal` | engine2 `0x7d15c0` | RE |
| `EncodeField` | networksystem `0x3334e0` | RE (string-anchor + prologue, **see §8**) |

(Function addresses are this-build only — resolve at runtime via string anchors / vtable.)

---

## 8. Re-deriving `EncodeField` on a game update (the reliable recipe)

The encoder entry has **no exported symbol** and no usable string of its own — but it spews
`"EncodeField encoder wrote %d bits"`. Anchor on that string, then walk back to the function entry.
Two earlier attempts were wrong; the failure modes are instructive:

- ❌ `0x4334d0` — a `CUtlVector` realloc helper (IMemAlloc Alloc/Realloc/GetSize). Wrong xref.
- ❌ `0x3356dd` — a **Ghidra image-base-offset** address. Ghidra rebases; its listing address is NOT
  the file vaddr. Subtracting wrong → landed mid-instruction (live bytes `fe ff ff c6…`, not a
  prologue). **Arming it would crash the server.** Always confirm the target is a real prologue.
- ✅ `0x3334e0` — the function entry, derived purely from `objdump` (file vaddr, no rebasing).

Recipe (build 2026-06-02, repeat per update):

```bash
SO=libnetworksystem.so
# 1. Find the string's vaddr.
readelf -p .rodata $SO | grep -n "encoder wrote"   # -> @ vaddr 0x15d580

# 2. Find the instruction referencing it (objdump annotates rip-relative targets with '# <vaddr>').
objdump -d $SO | grep -F '# 15d580'                # -> lea @ 0x3357fb

# 3. Walk back to the entry: nearest `push %rbp; mov %rsp,%rbp` above it that owns the frame.
#    Confirm by matching a frame slot — the xref site reads -0x290(%rbp); the entry must set it up.
objdump -d --start-address=0x333000 --stop-address=0x3357fb $SO | grep -B2 'mov +%rsp,%rbp'
#    -> 0x3334e0: push %rbp / mov %rsp,%rbp / ... / sub $0x2c8,%rsp / mov %r9,-0x290(%rbp) @0x33350b ✔

# 4. Extract a UNIQUE prologue signature and verify it matches exactly once.
objdump -s --start-address=0x3334e0 --stop-address=0x3334fe $SO
#    -> 55 48 89 E5 41 57 49 89 D7 41 56 41 55 41 54 41 BC 01 00 00 00 53 48 81 EC C8 02 00 00
```

Or, canonical/automated — run nosoop's makesig (or the headless Java port in `tools/`) which emits the
**shortest unique** sig, auto-wildcarding only ADDRESS/DYNAMIC operands. On this build it returns 21
bytes, zero wildcards (the prologue has no rip-relative/reloc operands), which is the unique prefix of
the objdump bytes above:

```
fn entry: 004334e0        # Ghidra addr = file-vaddr 0x3334e0 + 0x100000 image base
MATCHES: 1
IDA_SIG: 55 48 89 E5 41 57 49 89 D7 41 56 41 55 41 54 41 BC 01 00 00 00
```

This 21-byte sig is what ships in the gamedata. makesig's value shows when a prologue references a
global/string (rip-relative `lea`) — it masks those 4 displacement bytes so the sig survives a relink.

Cold-block layout caveat: `EncodeField` has an early `ret` (~0x33352e) followed by out-of-line blocks
(spew/error paths, incl. the string xref at 0x3357fb) sharing the frame. Don't mistake that early
`ret` for the function end when bounding it.

**Still pending:** dynamic confirmation (gdb breakpoint `NS_base + 0x3334e0`, real client doing a
visible netvar change like `ms_slap`) that this fires per field-encode + which arg register carries
the value pointer. See `sp_gdb3.py`.

---

## 9. The per-field VALUE encoder — FOUND (the actual detour target)

`EncodeField` (§2) only *dispatches*; the function that actually serializes an integer field's value
into the bitstream is at **`0x3c8e70`** (libnetworksystem.so, file-vaddr). This is the fn
`EncodeField` reaches via `*(*(fieldInfo + 0x38))` (vtable slot 0 of the dispatch object) for
integer-typed fields.

**Body:** canonical signed **zigzag** then **varint**:
```
zz = (n << 1) ^ (n >> 63)      // signed zigzag
bf_write_varint(buf, zz)        // varint writer @ 0x400890
```

**makesig (build 2026-06-14):**
```
48 8B 01 55 48 89 E5 48 8D 34 00
```
First instruction is `mov (%rcx),%rax` → the VALUE is read from `(%rcx)`.

**ABI — 5 register args, NO stack args (safe to detour):**

| reg | meaning |
|---|---|
| `rdi` | `bf_write*` (the output bitstream) |
| `rsi` | `fieldInfo` (`CNetworkSerializerFieldInfo*`) |
| `rdx` | (context, unused for the spoof) |
| `rcx` | **value pointer** — encoder reads `*(int*)rcx` |
| `r8d` | uint (flags/count) |

Because all args are in registers, an `IDetourHook` trampoline is safe (no stack-layout fixups).

---

## 10. The encoder registry (how the dispatch ptr gets populated)

The mapping from field encoder-name → encoder fn lives in a static header table at file-vaddr
**`0x45f360`** (Ghidra `PTR_PTR_0055f360`):

- **8 field-type buckets**, **0x80-byte stride** per entry.
- Each entry: `slot[0]` = encoder-name string (`char*`); `slot[6]` = encoder fn pointer.

**`FUN_0044b980`** (call it `InitFakeField`, file-vaddr **`0x34b980`**) is what wires a field to its
encoder: it name-matches a field's encoder name against the registry and copies slots
**`[6, 7, 1, 4, 5]`** into the dispatch block at `fieldInfo + 0x38`. The important one is
`slot[6] → dispatch vtable[0]`, i.e. exactly what `EncodeField` later calls.

**How the library uses this (gamedata-resolved classification).** Rather than hard-coding the table
address and walking `RegistryAddr + b*16` in C#, the gamedata file declares the registry table base
(`CFlattenedSerializer::EncoderRegistry`, sig + `+3 r` factory) and derives each per-bucket handler
base from it declaratively with the factory op-chain — `"base": "…EncoderRegistry", "factory": "+{b*16} d"`
(`+N` offset then `d` dereference; ops are defined in `Engine/src/gamedata.cpp`). At install the library
enumerates each resolved bucket handler's entries once (stride `0x80`, fn at `+0x30`), maps `(bucket,
encoder-name) → FieldType`, and cross-checks the bucket-1 `default` fn against the standalone
`CFlattenedSerializer::EncodeInt32` signature (warns on mismatch). The per-field hot path
(`FieldSubstitution.Classify`) is then a single dictionary lookup of the field's live dispatch fn
(`*(*(fieldInfo+0x38)+0x30)`) — no runtime string scraping of the registry on the send path.

**Full registry map (file-vaddr):**

| bucket | encoder name | fn |
|---|---|---|
| type1 | default (int / varint) | `0x3c8e70` |
| type1 | fixed32 | `0x3c3b10` |
| type1 | fixed64 | `0x3c40c0` |
| type3 | default | `0x3c4d70` |
| type3 | qangle | `0x3c6220` |
| type3 | normal | `0x3c5850` |
| type3 | coord | `0x3cfb40` |
| type3 | coord_integral | `0x3d0d20` |
| type3 | qangle_pitch_yaw | `0x3cf320` |
| type3 | qangle_precise | `0x3c9ed0` |

(The int32 default `0x3c8e70` is the Phase-1 detour target above.)

---

## 11. `fieldInfo` layout — CORRECTED

Two distinct structures were conflated in earlier notes. Keep them separate:

**(a) Flattened-serializer `fieldInfo`** — the argument `EncodeField`/the encoder receives. This is the
full, flattened field set, carries the encoder dispatch, and is what the detour reads.

- **`fieldInfo + 0x08`** = `char*` **field name** — the reliable identifier. It is
  **inheritance-proof**: it matches wherever in the `CBaseEntity → … → pawn` chain a field is declared
  (e.g. `m_iHealth` is present here even though it's a `CBaseEntity` field).
- **`fieldInfo + 0x38`** = encoder dispatch block (vtable slot 0 = the encoder fn).
- ⚠️ **`fieldInfo + 0x40` reads `0` in practice — it is NOT `m_nFieldOffset`.** An earlier note
  (and the old offsets table) claimed `+0x40` was the value offset; that was **wrong**. Do **not**
  filter fields on `+0x40`. Identify fields by the name at `+0x08` instead.

**(b) Per-class `CNetworkSerializerClassInfo` field records** — reached via entity `vtable[0]`
(`GetNetworkSerializerInfo`, §3). These are a *different, leaner* record:

- Hold only that class's **OWN** fields (e.g. **61** for `CCSPlayerPawn`), **no inherited fields** —
  so `m_iHealth` (declared on `CBaseEntity`) is **absent** here.
- Name @ `+0x08`; packed type/size @ `+0x38`.
- **No encoder pointer**, no recipients filter.

> Takeaway: the encoder dispatch + the complete flattened field set live on the
> **flattened-serializer** `fieldInfo` (the encoder's argument), NOT on the per-class records.
> Resolve via the encoder hook's `rsi`, not via the entity-vtable per-class info, when you need the
> encoder or an inherited field.

---

## 12. WORKING Phase-1 value spoof (uniform, all clients) — confirmed live

In-process ModSharp `IDetourHook` on the int32 encoder (`0x3c8e70`, §9). In the hook:

```
1. name = *(char**)(fieldInfo + 0x08)            // rsi + 0x08
2. if name != target (e.g. "m_iHealth") -> call trampoline unchanged
3. write the fake value into a stable native scratch int  (long-lived, not stack)
4. set rcx (arg d, the value pointer) = &scratch
5. call the trampoline
```

The encoder writes the fake into the bitstream; **entity memory is never touched** → a true
SourceMod-style send proxy (real server-side state stays real).

**Confirmed live:** clients saw **1337 HP** while real server-side HP stayed real.
Implementation: `Native/IntEncoderDetour.cs`, commands `sp_fakehp` / `sp_fakehp_off`.

---

## 13. Tooling / methodology lessons (2026-06-14)

**Live gdb over the slow remote gdbserver link proved unreliable for this work** — the reliable path
was static Ghidra + the in-process ModSharp detour. Specifics:

- **Never `kill -9` an attached gdb.** It leaves a half-open TCP in `FIN-WAIT-2`; gdbserver is
  single-client, so the next `target remote` hangs. Always **detach cleanly**.
- **`catch load <name>` does NOT fire** when `set sysroot /tmp/empty` + `auto-solib-add off` suppress
  named load events. Use `set stop-on-solib-events 1`, or SIGINT-to-break after boot, instead.
- **Booting the server *under* gdb over the link is very slow** (>150s → times out).
- **A `gdb -batch` session can't be introspected** without ending it.
- **Ghidra 11+/12 dropped bundled Jython** — write **Java** GhidraScripts (see `tools/`), not Python.
- **`modsharp-deploy` bundles assets from `.assets/` (plural)**, not `.asset/`.

---

## 14. Phase 2 — per-client VALUE (RESOLVED: send-path hook is impossible)

**Verdict (conclusive static RE, 2026-06-14): you cannot inject a per-client VALUE by hooking the
per-client send path — it copies pre-encoded bits and never re-runs the encoders.**

CS2 packs the snapshot ONCE, shared; the encoder (`0x3c8e70`) fires with `rsi = 0` (no client) during
that pack. The per-client send then deltas/copies from the single packed buffer:

- **`CNetworkGameServer::SendClientMessages`** (file `0x6a7280` / Ghidra `0x7a7280`; note **e2proj/nsproj
  Ghidra image base = file-vaddr + 0x100000**) packs once (threaded `CSendClientMessagesJob` /
  `PackEntities`), then loops clients (array @ `gameServer+0x4b`, count `+0x4a`) calling per-client
  **`SendSnapshot`** at **client-vtable `+0x80`** (current client *is* in scope here) with a per-client
  context built via `packedFrame+0x2b0`.
- **`CNetworkGameServerBase::WriteDeltaEntity_Internal`** (file `0x6d15c0`) operates on already-packed
  `PackedEntity` objects (`ctx+0x90` from-snap, `ctx+0x98` to-snap). It calls the serialization
  singleton `DAT_00ac4ae0` (file `0x9c4ae0`): `+0x40` = **CalcDelta** (changed-field count from the
  packed bit-buffers) and `+0x50` = **WriteFieldList** (blits the selected pre-encoded bit ranges into
  the client's bitbuf). **No call to `EncodeField` / `0x3c8e70` anywhere in this path** — values are
  copied, not re-encoded. (Matches the Phase-1 capture: encoders fire once, `rsi=0`.)

So a `thread_local` current-client trick can't work — the encoder is never invoked per client. The
only NATIVE per-client field mechanism is a **recipients bitmask** (`CFlattenedSerializer::
GatherSendProxyResults_R` nsys `0x1593d8`; `m_NetworkSendProxyRecipientsFilter` @ `fieldInfo+0x28`),
which gates *presence* (who receives the field), never its value.

### Options for per-client value (all require forcing the encoder to re-run)
1. **Per-client re-pack** — true per-client value, but N× encode cost (defeats the shared-pack
   optimization; brutal at 64p / 10k entities).
2. **Group-bucket re-pack** *(recommended)* — pack once per *value-group* (e.g. TTT traitor vs innocent),
   scaling with #groups not #clients. The practical sweet spot for role/team-based spoofing.
3. **Recipients-mask** — zero-cost but presence-only (hide a field from some clients; value stays uniform).

Next RE step if (1)/(2) is chosen: decompile the threaded pack job (`N16CHLTVServerAsync22CSend
ClientMessagesJobE` / `PackEntities_Normal`) to find where it can be driven once per group with a
`thread_local` client/group set, then read in the existing `0x3c8e70` encoder hook.

---

## 15. Phase 2 — per-client VALUE (REVISES §14: substitution at the per-field copy WORKS)

> **§14's "impossible" verdict was about re-running the *encoder*** — that remains true (the per-client
> send copies pre-encoded bits, it never invokes `EncodeField`/`0x3c8e70`). But you do **not** need the
> encoder to re-run. The per-field bit-**copy** in the send loop both (a) runs once **per client** and
> (b) knows the field identity — so a per-client value can be injected by **substituting at the copy**,
> not by re-encoding. This is the working Phase-2 mechanism; verified end-to-end in verify mode
> (recipient captured, `m_iHealth` resolved, cursor math correct, zero corruption).

All RVAs are **file-vaddr**; Ghidra image base = file-vaddr + 0x100000. Raw decompiles in
`re-dumps/` (esp. `sp_wfl.out`, `sp_bitprims.out`, `wde.out`/`wde.asm`, `perclient.out`); Ghidra
scripts in `ghidra_scripts/` (`SendProxyWDE.java`, `SendProxyPerClient.java`, `SPWriteFieldList.java`,
`SPBitPrims.java`, etc.).

### 15a. The per-client send path

| Function | RVA (file) | Role |
|---|---|---|
| `CNetworkGameServer::SendClientMessages` | engine2 `0x7a7280` | Pack the snapshot **once** (shared), then loop clients. |
| `CNetworkGameServer::PerClientEncode` | engine2 `0x8fae40` | The per-client encode entry. **`CServerSideClient*` in `rsi`** = the recipient. |
| `CNetworkGameServerBase::WriteDeltaEntity_Internal` | engine2 `0x6d15c0` | Per-client, per-entity delta write. **Parallel across ~6 worker threads.** |
| `CFlattenedSerializer::WriteFieldList` | nsys `0x343b60` | The per-field copy loop. |

- **`SendClientMessages`** packs all entities once into the shared `CFrameSnapshot`, then writes each
  client's delta from that single packed buffer. The encode happened during the shared pack
  (`EncodeField` with `rsi=0`) — it is **not** re-run per client.
- **`PerClientEncode`** (`0x8fae40`) is the per-client entry; the recipient `CServerSideClient*` arrives
  in **`rsi`**. We detour it and stash the recipient into a **`[ThreadStatic]`** (`RecipientCapture.cs`),
  cleared on exit. Everything that runs downstream on that worker thread (including `WriteFieldList` and
  the bit-copy) can read it without threading the client through every call frame.
- **`WriteDeltaEntity_Internal`** (`0x6d15c0`) is confirmed **per-client and parallel**: the
  `sp_wdeprobe` capture saw the same entity written once per client into distinct ctx/`bf_write` objects,
  across ~6 worker threads (tid 18–23). ctx (`rsi`): `+0x34` entIdx, `+0x88` `bf_write`, `+0x90`
  from-snap, `+0x98` to-snap. **Implication:** per-client value substitution belongs in *this* path —
  it rides the existing per-client write, so there is **no N× re-pack**. Thread-safety comes from
  per-call **locals** (never the shared Phase-1 `_scratch`, which breaks under 6 threads); only the
  captured client is `[ThreadStatic]`, and the serializer config is read-only.

**Why per-client VALUE can't be done by re-pack and IS done at the per-field copy:** the snapshot is a
single shared, entity-keyed pack produced by parallel worker threads; forcing it to vary per client
means re-running the encoders N× (§14 options 1/2). Substituting at the per-field **copy** avoids all of
that — the copy already runs per client, already has the recipient (via `PerClientEncode`/`rsi`), and
already has field identity (below). We rewrite that one field's bits in the client's own stream.

### 15b. The substitution mechanism (inside `WriteFieldList`)

`WriteFieldList` has a **two-stream layout**: field values are buffered into an intermediate
`bf_write` separately from the field-path stream, and the value buffer is appended at the end. Because
the value stream is decoupled from the path stream, a **variable-length** substitution (e.g. a varint
whose byte width differs from the original) is **safe** — it shifts only within the value buffer.

Per changed field the loop calls two functions back-to-back:

| Function | RVA (file) | Role |
|---|---|---|
| `GetBitRange` | nsys `0x326260` | Resolves the changed-field index → `[startBit,endBit)` in the source. **3rd arg = the field token.** Fires immediately before each copy → the detour captures the token. |
| `BitCopyPrimitive` (`FUN_00500b70`) | nsys `0x400b70` | **The per-field copy primitive — the hook point.** `byte copy(bf_write* dst, bf_read* src, uint bitcount)`; rdi=dst, rsi=src, rdx=bitcount. The `bf_write` bit cursor is at **`*(int*)(dst + 0x10)`**. It is a pure bulk copy — **no field identity of its own**, which is why identity must come from `GetBitRange`'s captured token + the serializer on the `WriteFieldList` entry. |
| `VarintWriter` (`FUN_00500890`) | nsys `0x400890` | The varint primitive used to emit our substitute. `void write(bf_write* dst, uint32 zigzag)`; rdi=dst, rsi=zigzag. **Self-zigzag**: `zigzag = (uint)((v<<1)^(v>>31))`. |

**The save-cursor / call-original / rewind / write sequence** (in the `BitCopyPrimitive` detour, Fake
mode):

1. Save the dst cursor: `savedCursor = *(int*)(dst + 0x10)`.
2. Call the original copy (advances both src and dst cursors, writes the real bits).
3. Rewind: `*(int*)(dst + 0x10) = savedCursor`.
4. Emit our value: `VarintWriter(dst, zigzag(fake))` — overwriting the just-copied real bits with the
   substitute starting at the saved cursor.

In **Verify** mode the original is called normally (no rewind) and only the cursor math is logged
(`cursorBefore`/`cursorAfter`), proving field detection + cursor reads with zero output change. Three
detours cooperate: a `WriteFieldList`-entry shim stashes the serializer (`rdi`), `GetBitRange` captures
the field token, and `BitCopyPrimitive` performs the substitution — all driven off the `[ThreadStatic]`
recipient from `PerClientEncode`.

### 15c. The `CFieldPath` token

`GetBitRange`'s 3rd argument is a packed **`CFieldPath`** — a 3-level nested path with a **−1 bias** per
level and an `0x7FF` mask:

```
token = ((i0+1) << 22) | ((i1+1) << 11) | (i2+1)        // pack
i0 = ((token >> 22) & 0x7FF) - 1                          // decode
i1 = ((token >> 11) & 0x7FF) - 1
i2 = ( token        & 0x7FF) - 1                          // -1 ⇒ level absent
```

Examples: `0x02810008` decodes to `[9, 31, 7]`; `0x01400000` decodes to `[4]` (i1 = i2 = −1, absent).
`0xFFFFFFFF` is the root sentinel (skip); `0` is "all absent" (skip).

### 15d. The flattened-serializer field-array layout (walking the path)

To resolve a token to a leaf field name, walk the serializer's field array (distinct from the per-class
`CNetworkSerializerClassInfo` records `sp_dump` reads via the entity vtable — see §11(b)):

- **array base** = `*(serializer + 0x30)` (a pointer stored at `+0x30`; deref once).
- **records are INLINE**, stride **`0x2E`** bytes (not a pointer array): `record_i = base + i*0x2E`.
- `record + 0x00` = `CNetworkSerializerFieldInfo*` (the **leaf** fieldInfo).
- `record + 0x08` = child `CFlattenedSerializer*` (for **descent** into a nested level).
- **leaf name** = `*(*(record + 0x00) + 0x08)` → `char*` (the `fieldInfo+0x08` name from §11).

Descent: at level 0 take `rec = base + i0*0x2E`; if `i1 ≥ 0`, `child = *(rec+0x08)`,
`base = *(child+0x30)`, `rec = base + i1*0x2E`; same again for `i2`. The leaf's name is read off
`*(rec+0x00)+0x08`. (Source: `encodefield.out` field-array refs; impl in `FieldSubstitution.cs`.)

### 15e. Known limit

**Nested-path descent still resolves empty for some fields** — when `i1`/`i2 ≥ 0`, the child-serializer
descent (`*(rec+0x08)` / `*(child+0x30)`) does not yet land on the right record for every field
(observed ~43/50 nested value-copies resolving to an empty name). **Top-level fields resolve cleanly**
(`m_iHealth`, `m_ArmorValue`, `m_nTickBase`), which is why those are the working Phase-1/Phase-2 targets.
The descent offsets need more RE before nested fields can be substituted reliably.

### 15f. Status & artifacts

Verify mode is confirmed (`SUBST-VERIFY` fired 8× for `CCSPlayerPawn::m_iHealth`: recipient captured,
`before=0`/`after=16`, `bitcount=16`, no desync). Implementation lives entirely in the core library's
`Native/FieldSubstitution.cs` (+ `Native/RecipientCapture.cs`); the engine addresses and encoder
identities are resolved from `.assets/gamedata/yappershq.sendproxy.jsonc`. The core library exposes the
mechanism only through `ISendProxyManager` and ships **no commands**. The example module
(`YappersHQ.SendProxy.Example`) drives it through AdminManager-gated commands (`sp_example_*`) and
carries the read-only serializer probe (`sp_probe_scan` / `sp_probe_dump` / `sp_probe_field`,
`SerializerProbe.cs`) used during this RE work. Raw decompiles preserved in `re-dumps/` (`sp_wfl.out`
is the key `WriteFieldList` reference); Ghidra scripts in `ghidra_scripts/`.
