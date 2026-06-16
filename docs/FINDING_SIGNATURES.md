# Finding & Re-Deriving SendProxy Signatures (Ghidra / IDA Pro)

This is a **practical guide for a human** maintaining `.assets/gamedata/yappershq.sendproxy.jsonc`. CS2
ships new `networksystem` / `engine2` binaries regularly; when an update breaks a signature you re-derive
it here. No prior knowledge of this plugin's internals is assumed — for *why* each function matters, read
[`REVERSE_ENGINEERING.md`](REVERSE_ENGINEERING.md); for *how to re-find them*, read this.

> TL;DR: most functions are found from a **unique log/assert string** → its **xref** → the **containing
> function** → take a **prologue byte pattern**, wildcarding relative offsets. The few with no usable
> string are found by **walking the call graph** (caller/callee xrefs) or by **walking the encoder
> registry table**. Always **verify** by decompiling the Linux and Windows builds side-by-side.

---

## 0. What you need

- **Binaries**: `libnetworksystem.so` + `libengine2.so` (Linux dedicated server, `/game/bin/linuxsteamrt64/`)
  and `networksystem.dll` + `engine2.dll` (Windows, `\game\bin\win64\`). Use the **same build** for both
  platforms when comparing.
- **Disassembler**: Ghidra (free) or IDA Pro. Both work. Examples below give the Ghidra-script form and the
  equivalent manual IDA steps.
- Let auto-analysis finish completely before searching (Ghidra: wait for the analysis bar; IDA: wait for
  "The initial autoanalysis has been finished").

### Image bases (so addresses in this repo line up)
- Linux `.so` in Ghidra: image base **0x100000** → an address printed as `0x1xxxxx` is file-RVA `+0x100000`.
- Windows `.dll` in Ghidra/IDA: image base **0x180000000**.
- Signatures are **position-independent** (FindPattern scans memory), so the absolute addresses below are
  only waypoints for a given build — the **byte patterns** are what go in the gamedata.

---

## 1. The signature format

ModSharp gamedata sigs are space-separated hex bytes; `?` is a single wildcard byte:

```
55 48 8D 05 ? ? ? ? 45 31 DB 66 0F EF C0 48 89 E5
```

Rules that keep a sig stable across rebuilds:
1. Start at the **function entry** (prologue). Prologues are the most stable bytes.
2. **Wildcard every relative/absolute operand** — `rel32` displacements in `lea`/`call`/`jmp`, and any
   `[rip+...]` offset. These shift on every recompile. Mask the operand bytes to `?`, keep the opcode bytes.
3. Take the **shortest pattern that is still unique** in that module. Too short → collides with other
   functions; too long → brittle. A good rule: extend the pattern byte-by-byte until `FindPattern` returns
   exactly one match, then stop (trailing wildcards trimmed).

The repo scripts automate steps 1–3 (see §6). In IDA you can do the same with the "Create signature" /
IDA-style pattern, or by hand: select the prologue, look at the hex, replace operand bytes with `?`.

---

## 2. The functions and how to locate each

| Gamedata key | Library | Primary locator |
|---|---|---|
| `CFlattenedSerializer::EncodeField` | networksystem | string `"CFlattenedSerializer::EncodeField encoder wrote %d bits"` |
| `CFlattenedSerializer::WriteFieldList` | networksystem | string `"CFlattenedSerializer::WriteFieldList fieldDataBuf"` |
| `CFlattenedSerializer::GetBitRange` | networksystem | string `"GetBitRange( %d -> %d ) end is before or same as start"` |
| `CFlattenedSerializer::EncoderRegistry` (+ buckets) | networksystem | string `"CNetworkSerializer: Unable to find network encoder named %s!"` → registry table-load `lea` |
| `CFlattenedSerializer::EncodeInt32` | networksystem | signed-int registry entry → its `call` target (thunk→real encoder) |
| `CFlattenedSerializer::BitCopyPrimitive` | networksystem | standalone leaf; reach via a BitCopy caller (WriteFieldList) → its bit-copy callee |
| `CNetworkGameServerBase::WriteDeltaEntity_Internal` | engine2 | string `"…WriteDeltaEntity_Internal merging changes added in %d additional fields!"` |
| `CNetworkGameServer::PerClientEncode` | engine2 | string `"WriteOOPVSDeltaEntities"` / `"Delta: [%d] deletions"` (it’s the fn holding them; calls WriteDeltaEntity_Internal) |
| `CNetworkGameServer::SendClientMessages` | engine2 | string `"ComputeClientPacks"` (split on Win — see §3) |

### 2a. String-anchored functions (the easy majority)

**Ghidra (manual):**
1. `Search → For Strings…`, filter for the anchor text. Double-click the result to jump to it in the
   Listing.
2. With the string selected, open `References → Show References to Address` (or press `Ctrl+Shift+F`).
3. Double-click the referencing instruction → you land inside the function that logs/asserts with that
   string. `Functions` window shows its entry; that entry is your sig start.

**IDA Pro (manual):**
1. `Shift+F12` (Strings window) → find the anchor → double-click.
2. In the string's disassembly, click the `DATA XREF` comment → jump to the `lea` that loads it.
3. `Ctrl+P` / scroll up to the function start (`proc near`). That's your sig start.

Then take the prologue pattern (§1). Example — `EncodeField` Linux:
`55 48 89 E5 41 57 49 89 D7 41 56 41 55 41 54 41 BC 01 00 00 00`; Windows:
`48 89 5C 24 ? 88 54 24 ? 48 89 4C 24 ? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ? 48 81 EC 20 01 00 00`.

### 2b. The encoder registry — string → `lea` → factory op-chain

The registry is a static table (8 buckets `{handler*, count}`, stride 0x10; each handler → encoder entries,
stride 0x80, `name @+0x00`, `fn @+0x30`). You don't sig the table directly (it's data); you sig the
**`lea` that loads it** and attach a **factory op-chain** that walks from the `lea` to the value you want.

1. Find the `"...Unable to find network encoder named %s!"` string → its function (`InitFakeField` /
   the registry initializer).
2. Inside that function, find the instruction that loads the table base — a `lea reg, [rip+disp]` whose
   target is the bucket table (Ghidra shows `-> DAT_...` / `PTR_PTR_...`; IDA shows `lea rax, off_...`).
   - Linux: `lea rax, [rip+disp]` → bytes start `48 8D 05 ? ? ? ?`.
   - Windows: `lea rdi, [rip+disp]` → bytes start `48 8D 3D ? ? ? ?`.
3. Sig that `lea` + a few following bytes for uniqueness, wildcarding the `rel32`.
4. Attach the **factory** (`Engine/src/gamedata.cpp` ops, applied left-to-right to the resolved address):
   - `+3` — skip the 3-byte `lea` opcode so we point at the `rel32`.
   - `r` — RIP-relative resolve: read the `rel32` and compute the absolute target (the table base).
   - `+{N}` — add a byte offset (bucket *b* lives at `b*16`, so `+16`, `+32`, …).
   - `d` — dereference (load the handler pointer out of the bucket slot).
   - So `EncoderRegistry` = `+3 r`; `EncoderBucketN` = `+3 r +{N*16} d`.

> Why a factory instead of sigging the data: ModSharp's `ParseAddresses` drops any entry with no signature
> and no refs (`gamedata.cpp:840`), and data addresses aren't stable sig targets — the `lea` is.

### 2c. No-string helpers — walk the call graph / the table

Some functions have **no usable string** (compilers inline them, or the only "string" is a vprof scope name
loaded from a pointer table that the disassembler doesn't track as a code xref). Find them structurally:

- **`BitCopyPrimitive`** — the GENERIC `(dst, src, bitcount)` bit-block copy. It is NOT reached from a field
  encoder (that route dead-ends in a varint writer that inlines its own bit ops — do not be fooled). Route:
  decompile the **Linux** BitCopyPrimitive (prologue `55 48 89 F0 41 89 D0 48 89 E5 41 57 41 56 41 55`),
  list its callers (WriteFieldList / MergeDeltas / BuildMergedSerializedEntity / the GetBitRange iterator).
  Take a caller with a distinctive string (e.g. `"…WriteFieldList fieldDataBuf"`), find its **Windows** twin,
  decompile it, and the `(dst,src,bitcount)`-shaped leaf it calls IS `BitCopyPrimitive`. Confirm by listing
  *that* leaf's callers — you should see the same 4 functions. (It is standalone on Windows, not inlined.)
- **`EncodeInt32`** — the signed-int field encoder. On Windows the bucket entry is a small **thunk**
  (`sub rsp,0x28; mov rdx,[r9]; call <real>`) — follow its single `call` to the real encoder (the body is a
  zigzag transform `(n<<1)^(n>>63)` then a tail-jmp into the varint writer).
- **`WriteDeltaEntity_Internal` / `PerClientEncode`** — these two are a callee/caller pair and EASY to
  confuse. The SINGULAR writer `WriteDeltaEntity_Internal` is found by its own unique string
  `"…WriteDeltaEntity_Internal merging changes added in %d additional fields!"`. `PerClientEncode` is the fn
  that holds `"WriteDeltaEntities"` / `"WriteOOPVSDeltaEntities"` / `"Delta: [%d] deletions"` and *calls*
  the singular writer — find it by those strings, confirm it calls `WriteDeltaEntity_Internal`.
- **`SendClientMessages`** — the per-client send/pack loop. Its `"SendClientMessages"`/`"PackWork_t"` vprof
  names have no code xref, but the body strings `"ComputeClientPacks"` / `"PackEntities_Normal …"` /
  `"PrepareSendClientMessages"` do. NOTE: on Windows this Linux monolith is **split** across multiple fns —
  there is no single 1:1 twin; pick the representative (ComputeClientPacks owner) or resolve via
  `refs.strings`. (The runtime doesn't actually hook this one, so an imperfect mapping is harmless.)

**Ghidra**: callers via `References → Show References to` on the function entry, filter to CALL refs.
**IDA**: `Ctrl+X` (xrefs to) on the function name, or the function's "Xrefs" pane.

---

## 3. Linux vs Windows — gotchas that will bite you

- **Calling convention differs.** SysV (Linux) passes args in `rdi, rsi, rdx, rcx, r8, r9`; Win64 in
  `rcx, rdx, r8, r9` + 32 bytes of shadow space and arg spills (`mov [rsp+X], rcx` …). So a Windows prologue
  looks nothing like the Linux one — **that is expected**, sig each platform independently. It does **not**
  mean you found the wrong function.
- **Struct member offsets are the SAME** on both platforms (Source2 compiles the same struct definitions).
  If your Windows decompile seems to show a different offset for the same field, suspect a **misread**
  (adjacent field, or the decompiler split a struct) before concluding the layout diverged. This was
  checked for SendProxy — the three apparent "mismatches" were all decompiler artifacts; see the OFFSET
  NOTES block at the bottom of the gamedata file.
- **Inlining differs.** MSVC inlined `GetBitRange` into all ~8 of its callers — there is **no standalone
  `GetBitRange`** on Windows. Its assert string xrefs resolve to the callers (incl. `WriteFieldList`), not
  to a dedicated function. When a Linux helper has no standalone Windows entry, document it and hook the
  caller that contains the inlined body instead of inventing an address.
- **String xref may be empty even though the function exists.** If `getReferencesTo(string)` returns
  nothing, the string is likely loaded via a pointer table (vprof) — fall back to the call-graph walk (§2c).

---

## 3d. Windows findings for THIS build (verified by side-by-side decompile)

Concrete results from the 2026-06 `csgo_rel_win64` build (addresses are waypoints; sigs are in the
gamedata). All confirmed `same_function` against the Linux twin unless noted.

| Function | Windows addr | Notes |
|---|---|---|
| EncodeField | `0x1800661a0` | offsets identical to Linux (+0x38 encoder dispatch, +0xC9 params, bf_write +0x00/+0x10/+0x20, record stride 0x2e) |
| WriteFieldList | `0x18006f970` | contains the inlined GetBitRange assert |
| EncoderRegistry init | `0x180073ae0` | table-load `lea rdi,[rip+…]` @`0x180074328` → table `0x18026b480` (8 buckets, counts 1/3/3/7/1/1/1/1) |
| EncodeInt32 (signed) | `0x1801b7600` | zigzag wrapper → tail-jmp into varint writer |
| BitCopyPrimitive | `0x1801b6ec0` | **standalone**, 4 callers (WriteFieldList/MergeDeltas/BuildMergedSerializedEntity/GetBitRange-iter) |
| WriteDeltaEntity_Internal | `0x1800d1240` | singular per-entity writer; 11/11 param-struct offsets match Linux |
| PerClientEncode | `0x1800d2e90` | per-client encode; calls WriteDeltaEntity_Internal; RecipientCapture hook target |
| GetBitRange | *(none)* | **inlined** into ~8 callers — no standalone entry (see below) |
| SendClientMessages | *(split)* | Linux monolith → Windows split (ComputeClientPacks `0x1800e8ce0`, PackEntities_Normal `0x1800e81e0`); unused by runtime |

**Two Windows traps that wasted time — learn from them:**
1. **`0x1800d2e90` is PerClientEncode, NOT WriteDeltaEntity_Internal.** It holds the `WriteDeltaEntities`/
   `WriteOOPVSDeltaEntities` strings (which on Linux live *inside* PerClientEncode). A naive "find the fn
   with the WriteDelta string" lands here. The actual singular writer is `0x1800d1240`, found by its own
   unique `"…merging changes added in %d additional fields!"` string. Caller (PerClientEncode) vs callee
   (WriteDeltaEntity_Internal) — don't conflate.
2. **The int→encoder route does NOT lead to BitCopyPrimitive.** Following the signed-int registry entry
   reaches a *varint writer* that inlines its own bit ops — a dead end that looks like "BitCopyPrimitive is
   inlined." It isn't: BitCopyPrimitive is the *generic* bit-block copy on the WriteFieldList/MergeDeltas
   path (`0x1801b6ec0`). Route via a known BitCopy caller, not via an encoder.

**Inlining / splitting differs from Linux** (the compiler, not the layout): `GetBitRange` is inlined into
all its callers (no standalone Windows fn). `SendClientMessages` is one ~10KB Linux fn but several smaller
fns on Windows. When a Linux function has no clean Windows twin, that's a real compiler difference — record
it and hook the enclosing/representative function rather than forcing a 1:1 address.

## 4. Verifying a signature (do this every time)

1. **Uniqueness**: `FindPattern` (or IDA `Search → Sequence of bytes`) must return **exactly one** hit in
   the target module. Zero → too specific / operand not wildcarded. Many → too short.
2. **Side-by-side decompile**: decompile the Linux fn and the Windows fn and confirm they implement the
   **same logic** — same control-flow shape, same **struct offsets** applied to pointers, same magic
   constants/strings, same call structure (ignoring the ABI register differences from §3). If they don't
   match, you sigged the wrong Windows function — redo it.
3. **Offset double-check**: for any `+0xNN` the C# side relies on (e.g. `fieldInfo+0x38` encoder dispatch,
   `+0xC9` params byte-offset, `bf_write+0x10` cursor-bits), confirm the **same constant** appears in both
   decompiles. Cross-check writes too: e.g. the registry init **writes** the encoder fn into
   `*(fieldInfo+0x38)`, which independently proves +0x38 is the encoder slot.
4. **Boot sanity-check**: the plugin re-resolves every gamedata sig at load and logs failures — load it on
   a real server of each platform and watch the log before trusting a new sig in production.

---

## 5. Updating the gamedata file

Each entry is `{ "library": "...", "linux": "<sig>", "windows": "<sig>" }`. A registry/bucket entry uses the
object form `{ "signature": "...", "factory": "..." }` per platform. Keep **both** platforms populated; if
one genuinely can't be resolved, leave a `// TODO <platform>:` with the concrete reason (not a blank, not a
guess). Never modify a working platform's value while adding the other.

---

## 6. The helper scripts in this repo (`/ghidra_scripts/`, Ghidra headless)

These automate the above for Ghidra; IDA users follow the manual steps in §2. Run read-only so multiple can
share one project:

```
$GHIDRA/support/analyzeHeadless <projDir> <projName> -process <binary> \
    -noanalysis -readOnly -scriptPath /home/claude/ghidra_scripts -postScript <Script>.java [args]
```

| Script | Purpose |
|---|---|
| `WinSigs.java` | Batch: for each known anchor string, find the fn and emit a shortest-unique prologue sig. |
| `WinResolve.java` | Registry table walk (buckets + encoder fns + sigs), the table-load `lea`, EncodeField call list, GetBitRange xref count. |
| `WinDecomp.java` | Decompile the anchor functions (for reading the logic). |
| `DecompBySig.java <sig\|0xADDR>` | Decompile whatever a sig (or address) lands on — use on Linux *and* Windows projects to compare. |
| `WinCallers.java 0xADDR` | List a function's callers with sigs — for the call-graph walk (§2c). |

> These are Ghidra-specific conveniences, not required: every result is reproducible by hand in IDA Pro or
> Ghidra's GUI using §2. The signatures, not the tooling, are the deliverable.
