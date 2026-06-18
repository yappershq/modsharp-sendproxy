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
  discarded) + its now-unused key const. The gamedata entry was also removed (it was never resolved at runtime).
- **Removed the no-op `ProxyManager.Clear()`** and its `Shutdown` call (cleanup lived in
  `FieldSubstitution.ClearAll()`/`Uninstall()`; `Clear()` was a misleading stub).

## Reviewer over-flags — dismissed with evidence (NOT bugs)

- **"`IVirtualHook` not `Dispose()`d"** — `IVirtualHook : IRuntimeNativeHook`, which is **not**
  `IDisposable` (unlike `IDetourHook`/`IMidFuncHook`). `Uninstall()` is the correct + only cleanup. No fix.
- **"Module should be `internal sealed`"** — `public sealed class : IModSharpModule` is the dominant
  convention across the existing plugins (≈129 public-sealed vs 12 internal). The loader instantiates the
  public type. Kept public.
- **"`Float32` writes `*(double*)` — bit-corruption"** — intentional: the float32 *encoder's* value-ptr
  convention is `*(double*)` (it reads a double and narrows), distinct from the Coord/Normal/quantized cases
  which use `(float*)[]`. Each `FieldType` writes the scratch in the exact width its encoder reads. No fix.

## Recommended pre-merge polish

APPLIED (commit f313108):
- **`IsValidEntity` guards** on every entity-scoped `Hook`/`SetEntity`/`Unhook` overload (guard before
  reading `entity.Index` — a stale entity wrapper is a dangling native pointer); `BeginSendFake`'s null
  check upgraded to the same.
- **`SetOneShot(bool)` overload** added for parity with the typed spoof API.

REMAINING (cosmetic, deferred):
- **`FieldSubstitution.SetFieldPathRegister` / `FieldPathReg`** — Windows-tuning scaffolding; mark
  `internal` + `TODO(windows)`, or expose via `IProxyManager` if Windows tuning becomes near-term.
- **Registration-API visibility** — `SetSpoof`/`SetCallback`/`SetOneShot` are `public static` on an
  `internal` class (already effectively assembly-internal, so this is purely stylistic — low value).
