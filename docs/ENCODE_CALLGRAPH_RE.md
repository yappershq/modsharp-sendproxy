# Encode call-graph RE — crash-safe entity/serializer capture (2026-06-17)

Goal: find a **crash-safe** site to capture the entity (+ serializer) being encoded during the shared
`PackEntities` pass, so the once-per-field proxy callback can know which entity it's firing for — WITHOUT
detouring the function that stack-overflowed (`CFlattenedSerializer::Encode`).

All addresses are libnetworksystem.so file-vaddr RVAs (Ghidra image base = +0x100000). Derived offline via
objdump; no server interruption.

## The crash, explained

`CFlattenedSerializer::Encode` @ **0x33afe0** (the fn carrying the `"CFlattenedSerializer::Encode failure
for entity %d"` xref @0x33b345):

- **~100 KB stack frame** — prologue does `sub rsp, 0x18b58` (100,696 bytes of locals).
- **17 indirect (vtable) calls** in its body (0x33afe0–~0x33bcdb, ~3.3 KB code). It encodes each field by an
  indirect dispatch; **embedded/nested fields re-enter `Encode` itself via those indirect calls** →
  **indirect recursion** (which is why a `call 0x33afe0` self-reference grep came back clean).
- Net: recursion depth × 100 KB frame × (added managed detour frame) → **stack overflow on join**.

→ **Never detour `Encode`.** A re-entrancy guard wouldn't even help: the 100 KB `sub rsp` is in the
trampoline-executed original body, so every recursion level still allocates 100 KB.

## The call graph

```
SendClientMessages
  └ CNetworkGameServer::PackEntities                          (per-FRAME)
      └ PackEntities_Normal(CUtlVector<Entity2Networkable_t*>&, …, CServerSideClient**,
      │                     CBitVec<16384>& /*PVS*/, …, CFrameSnapshot*)   (per-FRAME)
      └ CParallelProcessLauncher::ParallelForEach(PackWork_t lambda)        ← PARALLEL across entities
          └ [per-work lambda]  ──vtable──▶
  0x38a130   ← per-ENTITY wrapper (granularity bottoms out here).  SMALL frame (sub rsp,0x88).  ONE call to Encode (@0x38a1d0).  ret @0x38a22d.
        │      args: rdi, rsi, rdx, rcx, r8d = ENTITY INDEX (passed straight into Encode), r9d.
        │      r14 = *(rsi+0x8)  → passed as Encode's rdi (the serializer/field-set).
        ▼
  0x33afe0  CFlattenedSerializer::Encode   ← 100 KB frame, indirectly RECURSIVE (embedded fields).  DO NOT HOOK.
        │  (17 indirect calls: per-field leaf encoders + recursive Encode for embedded serializers)
        ▼
  leaf field encoders (the gamedata EncoderBucket1..7 fns, hooked by UniformEncoderHook)  ← proxy dispatch fires here
```

Key facts established:
- The **leaf encoders fire within `Encode`'s call tree**, which is within `0x38a130`'s call tree → a
  thread-local set at `0x38a130` entry is valid when the leaf encoders run.
- `0x38a130` calls `Encode` **once** (single-shot per entity); the recursion is `Encode → Encode`, NOT
  through `0x38a130` → **`0x38a130` is the non-recursive per-entity entry**.
- `0x38a130` is reached **indirectly** (via the entity's vtable from engine2's parallel pack) — no direct
  callers in networksystem; consistent with "called once per dirty entity by the pack loop."

## Chosen safe capture site

**Detour `0x38a130`** (gamedata: `CFlattenedSerializer::EncodeEntity` or similar — resolve by the call-into-
`Encode` site / a prologue sig). On entry, stash into `[ThreadStatic]`:
- **entity index** = `r8d` (5th SysV arg) — confirmed: it's the value `Encode` formats into the
  "...for entity %d" log.
- **serializer** = `*(rsi+0x8)` (= the ptr handed to `Encode` as rdi; read the serializer name at +0x00 at
  leaf-encode, same layout the WriteFieldList shim used).

Why it's crash-safe (proof):
1. **Small frame** (0x88, ~136 B) vs Encode's 100 KB — no frame-driven overflow.
2. **Non-recursive** — single Encode call; the indirect recursion stays inside `Encode`, never re-enters
   `0x38a130`.
3. **Belt-and-suspenders:** add a `[ThreadStatic]` re-entrancy guard (capture on outermost entry only,
   minimal passthrough otherwise) — so even if a future build did re-enter it, a 136-byte frame × depth
   cannot overflow.

Thread-safety (matches MS req): PackEntities packs entities in parallel, but each entity is on **one**
thread; the capture is `[ThreadStatic]` (per-thread), and the proxy fires once per (entity, field) — never
concurrently for the same field, no shared "current recipient" state. Race-free by construction. The
callback still runs on a job thread (inherent to firing in PackEntities) → consumer contract = pure/read-only.

## Exhaustive candidate table (2026-06-18) — every interception point evaluated

Frames/recursion measured by binary scan (objdump + byte analysis of libnetworksystem.so / libengine2.so).

| Candidate | Addr (ns.so) | Frame | Recursive? | Stage / thread | Context available | Hook-safe? | Notes |
|---|---|---|---|---|---|---|---|
| PackEntities_Normal | engine2 | — | no | shared pack, per-FRAME | entity LIST, clients, PVS, snapshot | n/a | too coarse (all entities at once); parallel launcher |
| `ParallelForEach(PackWork_t)` lambda | engine2 | small | no | shared pack, per-work (parallel) | entity (Entity2Networkable_t) | maybe | synthesized lambda; calls 0x38a130; engine2-side |
| **0x38a130 (per-entity wrapper)** | 0x38a130 | **0x88** | **no** | shared pack, **per-entity (1/thread)** | **entity idx (r8d) + serializer (*(arg2+8))** | **YES ★** | single Encode call; ret @0x38a22d; the capture site |
| EncodeField | 0x3324e0 | 0x2C8 | **YES (4 self-calls)** | shared pack, per-field | fieldInfo, valuePtr, ctx | no | recurses for embedded fields → detour re-enters |
| Encode | 0x33afe0 | **0x18b58 (100KB)** | **YES (indirect, 17 vtable)** | shared pack, per-entity+nested | serializer, entity idx | **NO** | the crash; 100KB×depth |
| WriteFieldList | 0x342b60 | **0x18C38 (101KB)** | no | field-list serialize | serializer | no | huge frame → unsafe to detour |
| leaf bucket encoders | (gamedata buckets) | small | no (terminal) | shared pack, per-field | fieldInfo, value ptr | **YES ★** | where SetAll substitutes (UniformEncoderHook — proven safe in prod) |
| WriteDeltaEntity_Internal / BitCopyPrimitive | engine2 / ns | (per-client) | no | **per-client delta** (post-pack) | entity idx + **recipient** + value | **YES ★** | where SetFor (per-viewer) applies (proven safe in prod) |

## Recommended intercept design (confirmed by exhaustive RE)

No site beats the three-point split already in the rewrite — only the *entity capture* needs moving off the
recursive `Encode`:

1. **Entity + serializer capture → `0x38a130`** (the ONLY small-frame, non-recursive, entity+serializer-
   bearing per-entity site). + a `[ThreadStatic]` re-entrancy guard as belt-and-suspenders.
2. **Uniform `SetAll` value substitution → leaf bucket encoders** (UniformEncoderHook — already production-
   proven; shared-pack, per-field, non-recursive).
3. **Per-viewer `SetFor` → per-client delta path** (BitCopy + RecipientCapture + WdeEntityCapture — already
   production-proven; that's the only stage with the *recipient*).

Everything else is disqualified: `Encode`/`WriteFieldList` (≈100KB frames), `EncodeField` (self-recursive),
PackEntities_Normal (per-frame, too coarse). The `PackWork_t` lambda is the only same-level alternative to
`0x38a130` but is engine2-side + synthesized; `0x38a130` is the cleaner, already-pinned target.

## Next (implementation, after this RE is accepted)
- Add a gamedata entry for `0x38a130` (linux prologue sig `55 48 89 E5 41 57 41 56 41 55 41 54 … 48 83 EC 88`
  + windows twin) — verify unique.
- Replace `EncodeCapture`'s target (Encode → `0x38a130`); read entity=r8d, serializer=*(arg2+8); add the
  re-entrancy guard.
- Build, then ONE careful live re-test on ttt (expect no crash this time, with the RE backing it).
