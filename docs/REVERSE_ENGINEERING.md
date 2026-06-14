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
- `valuePtr` = the value's address in entity memory (`field+0x40` value offset, with a secondary
  byte adjust at `field+0xC9` when `!= 0xFF`).
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
| `CNetworkSerializerFieldInfo` value offset (`m_nFieldOffset`) | **+0x40** | RE |
| └ secondary adjust byte | **+0xC9** (0xFF = none) | RE |
| `CNetworkSerializerClassInfo` → `m_Fields` (CUtlVector) | after filter+hash+classname | **confirm via dump** |
| `CFlattenedSerializer` name / count / field-array | +0x00 / +0x08 / +0x10 (ptr array, stride 8) | RE (WriteFieldList) |
| `SendClientMessages` | engine2 `0x7a7280` | RE |
| └ per-client SendSnapshot | vtable +0x80 | RE |
| `WriteDeltaEntity_Internal` | engine2 `0x7d15c0` | RE |
| `EncodeField` | networksystem `0x4334e0` | RE |

(Function addresses are this-build only — resolve at runtime via string anchors / vtable.)
