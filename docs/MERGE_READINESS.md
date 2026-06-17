# Merge-readiness review (pre ModSharp `.Core`/`.Shared` bundle)

Review of the plugin for bundling into ModSharp core. Findings triaged; the verified-safe cleanups were
applied, reviewer over-flags were dismissed with evidence, and the remaining items are recorded as
recommended pre-merge polish.

## Applied (verified safe, no behaviour change to the validated path)

- **`NativeUtil.IsUserPtr` is now platform-aware.** It was Linux-only (`(p>>40)==0x7F`), which would
  mis-classify every Windows pointer and silently break the safety gate on Windows paths. Linux branch is
  byte-identical; added a Windows branch (`p>0x10000 && (p>>48)==0`). The gate is hot, so the OS check is a
  cached `static readonly bool`.
- **Removed hot-path diagnostic logging in `RecipientCapture`** (the `RECIP#N` first-30 log + the `_count`
  field) — diagnostic scaffolding, not production instrumentation.
- **Removed the dead `SendClientMessages` resolution** (`ResolveFromGameData` call whose result was
  discarded) + its now-unused key const. The gamedata entry stays (documented as resolved-but-unused).
- **Surfaced the `ForceResend` `injected`/`skippedFull` counters** — now logged on `Uninstall` instead of
  being incremented-but-never-read.
- **Removed the no-op `SendProxyManager.Clear()`** and its `Shutdown` call (cleanup lived in
  `FieldSubstitution.ClearAll()`/`Uninstall()`; `Clear()` was a misleading stub).

## Reviewer over-flags — dismissed with evidence (NOT bugs)

- **"`ForceResend` `IVirtualHook` not `Dispose()`d"** — `IVirtualHook : IRuntimeNativeHook`, which is **not**
  `IDisposable` (unlike `IDetourHook`/`IMidFuncHook`). `Uninstall()` is the correct + only cleanup. No fix.
- **"Module should be `internal sealed`"** — `public sealed class : IModSharpModule` is the dominant
  convention across the existing plugins (≈129 public-sealed vs 12 internal). The loader instantiates the
  public type. Kept public.
- **"`Float32` writes `*(double*)` — bit-corruption"** — intentional: the float32 *encoder's* value-ptr
  convention is `*(double*)` (it reads a double and narrows), distinct from the Coord/Normal/quantized cases
  which use `(float*)[]`. Each `FieldType` writes the scratch in the exact width its encoder reads. No fix.

## Recommended pre-merge polish (not applied — defensive / cosmetic)

- **`IsValidEntity` guards on the entity-scoped `Hook`/`SetEntity`/`SendFake` overloads.** They read
  `entity.Index` directly; a stale entity wrapper is a dangling native pointer. Add
  `if (entity is not { IsValidEntity: true }) return;` at each entry (≈14 sites — best done via a small
  `bool TryEntityIndex(IBaseEntity?, out int)` helper to avoid duplication). Not a live bug (the plugin's
  own callers pass valid entities) but good public-API hygiene for a core merge.
- **`FieldSubstitution.SetFieldPathRegister` / `FieldPathReg`** — Windows-tuning scaffolding with no caller;
  mark `internal` explicitly + `TODO(windows)`, or expose via `ISendProxyManager` if Windows tuning is near.
- **Registration-API visibility** — `SetSpoof`/`SetCallback`/`SetOneShot` are `public static` on an
  `internal` class (effectively assembly-internal); make them `internal` to signal intent for the merge.
- **`SetOneShot` bool overload** — add for parity with the rest of the typed spoof API.
