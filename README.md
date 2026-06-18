# SendProxy for ModSharp (CS2)

**Per-field network value substitution** for **ModSharp / Counter-Strike 2 (Source 2)**. Intercept
what clients *receive* for a networked entity field and substitute the value — **without changing the
real server-side state**. (SourceMod's `SendProxyManager` was a conceptual reference only; the API is
ModSharp-native — see the unified `SpoofValue` API below.)

A consumer plugin resolves the `IProxyManager` interface and registers a `ProxyCallback` for a
`(serializerName, fieldName)` pair. The callback fires **once per (entity, field)** during the
shared `PackEntities` pass — never per client — and uses `SetAll` / `SetFor` to declare the
substituted value. Three targeting modes:

- **Uniform** (`SetAll`) — every client sees the same substituted value for that field.
- **Per-client** (`SetFor`) — each call to `SetFor` in the callback gives a specific client its own
  value; clients without an override see the real value. Per-viewer effects (ESP, etc.).
- **Per-entity** — the `Register(entity, …)` overload scopes a proxy to a single `IBaseEntity`;
  wins over the all-entities proxy when both exist.

Values flow through one `SpoofValue` type (factories `SpoofValue.Int/Float/Bool/Vector/String/Bytes`;
typed `v.AsInt`/`v.AsFloat`/… accessors) — a single API surface for every field type.

> **Why this is hard on CS2:** Source 1's SendProxy swapped each `SendProp`'s `m_pProxyFn`, a
> per-field function the engine called *while serializing each client's snapshot* — so per-client
> values were free. Source 2 has none of that: CS2 packs **all** entities **once** into a shared
> `CFrameSnapshot`, then writes each client a delta that **copies pre-encoded bits**. Values are
> global; native per-client differentiation is only *presence* (a recipients bitmask), not *value*.
> This library substitutes per-client by rewriting the per-field bits in the send loop. Full
> analysis: **[`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)**.

> **New here?** Read **[`docs/HOW_IT_WORKS.md`](docs/HOW_IT_WORKS.md)** first — a plain-English
> walkthrough of why this is hard on Source 2, the two substitution paths, the encoder families, the
> API, and the gotchas.

## Supported field types

| Category | Types | Status |
|---|---|---|
| Signed integer | int8 / int16 / int32 / int64 | substitutable (zigzag varint) |
| Unsigned integer | uint8 / uint16 / uint32 / uint64 | substitutable (raw varint) |
| Fixed-width integer | fixed32 / fixed64 | substitutable |
| Boolean | bool | substitutable (1 bit) |
| Float | float32 | substitutable (32 raw bits) |
| Angle / vector | qangle, vector3 | substitutable (3 contiguous float32) |
| String | null-terminated string | substitutable (7-bit encoded, null-terminated) |
| Byte array | raw byte array | substitutable (varint count + count raw bytes) |
| Float-derived | coord, normal, coord_integral, quantized float | **passthrough** (not yet substitutable) |

If a field's type can't be classified, the hook passes through untouched — it never writes
wrong-type bits, so an unsupported field is a no-op, not corruption.

Field classification is anchored on **gamedata-resolved encoder identities**: the encoder-registry
table and its per-bucket handler bases are declared in `yappershq.sendproxy.jsonc` (the per-bucket
entries derive their address from the table base via the gamedata factory op-chain, `base` + `+N`/`d`).
The library enumerates the encoder functions once at install from those resolved bases (cross-checking
the int32 encoder against its own standalone signature) and builds a fn-pointer → type map; the
per-field hot path is then a single dictionary lookup of the field's live dispatch fn. There is no
heuristic runtime registry walk on the send path.

## Consumer API — `IProxyManager`

### Resolving the interface

`IProxyManager` is published in the library's `PostInit`. ModSharp does **not** order
`PostInit`/`OnAllModulesLoaded` across plugins, but it does guarantee every plugin's `PostInit`
finishes before any `OnAllModulesLoaded` fires — so resolve the interface in **`OnAllModulesLoaded`**,
not `Init`/`PostInit`. Cache the reference for the plugin's lifetime.

```csharp
private IProxyManager? _proxy;

public void OnAllModulesLoaded()
{
    _proxy = _modules
        .GetOptionalSharpModuleInterface<IProxyManager>(IProxyManager.Identity)?.Instance;

    if (_proxy is null)
        _logger.LogWarning("SendProxy not loaded — feature disabled");
}
```

### Method surface

All registration is through two overload families:

- `Register(serializerName, fieldName, callback)` — proxy for every entity of that serializer.
- `Register(entity, serializerName, fieldName, callback)` — proxy scoped to one `IBaseEntity`
  (wins over the all-entities proxy for that entity).
- `Unregister(serializerName, fieldName)` — remove the all-entities proxy for this field.
- `Unregister(entity, serializerName, fieldName)` — remove the proxy scoped to one entity.
- `IsRegistered(serializerName, fieldName)` — returns `true` if any proxy exists for that field.

The callback delegate is `ProxyCallback`: `public delegate void ProxyCallback(ref ProxyContext ctx)`.
The `ProxyContext` carries `Entity`, `Field` (`.Name` + `.Kind`), `Original` (the real value the
engine would send), and the `SetAll`/`SetFor` output methods.

`SpoofValue` is the value type used everywhere — build one with `SpoofValue.Int(…)` / `.Float(…)` /
`.Bool(…)` / `.Vector(…)` / `.String(…)` / `.Bytes(…)`; read its contents with `.AsInt` / `.AsFloat` /
`.AsBool` / `.AsVector` / `.AsString` / `.AsBytes`. `.Kind` is a `SpoofKind` enum: `Int, Float, Bool,
Vector, String, Bytes`.

### Performance & thread-safety

The callback fires **once per (entity, field)** during the shared `PackEntities` pass —
**O(1) in player count**, never per recipient. `SetAll` writes the shared snapshot so it enters
every client's delta naturally (no extra cost). `SetFor` overrides are applied at the per-client
bit-copy stage — still only one managed call per entity per tick.

Callbacks run on a **pack worker thread**. They are never invoked concurrently for the same
(entity, field), so there is no race on their own local state, but the callback **must not** call
main-thread-only engine APIs or mutate shared state without guards — read schema / compute values
only.

### Uniform proxy (all clients, all entities)

```csharp
// Every client sees 1 HP for every player pawn; real m_iHealth still drives damage/death.
_proxy.Register("CCSPlayerPawn", "m_iHealth", static (ref ProxyContext ctx) =>
{
    ctx.SetAll(SpoofValue.Int(1));
});

// Force a string field to a fixed value for all clients.
_proxy.Register("CCSGameRulesProxy", "m_szMatchStatTxt", static (ref ProxyContext ctx) =>
{
    ctx.SetAll(SpoofValue.String("custom text"));
});
```

### Per-client (per-viewer) proxy

`SetFor` lets the callback give specific clients a different value. Clients that do not receive a
`SetFor` call see the real value.

```csharp
// Only `viewer` sees this pawn's health as 1 — every other client sees the real value.
_proxy.Register(pawn, "CCSPlayerPawn", "m_iHealth", (ref ProxyContext ctx) =>
{
    ctx.SetFor(viewer, SpoofValue.Int(1));
});

// All-entities: every pawn, but still only viewer sees the fake.
_proxy.Register("CCSPlayerPawn", "m_iHealth", (ref ProxyContext ctx) =>
{
    ctx.SetFor(viewer, SpoofValue.Int(1));
});
```

### Per-entity proxy

```csharp
// Scope a proxy to one specific entity; other entities of the same serializer are unaffected.
_proxy.Register(someEntity, "CCSPlayerPawn", "m_iHealth", static (ref ProxyContext ctx) =>
{
    ctx.SetAll(SpoofValue.Int(1));
});

// Per-client + per-entity.
_proxy.Register(someEntity, "CCSPlayerPawn", "m_iHealth", (ref ProxyContext ctx) =>
{
    ctx.SetFor(viewer, SpoofValue.Int(1));
});
```

`someEntity` is an `IBaseEntity` reference resolved at registration time. Do not store `IBaseEntity`
long-term — resolve it fresh, call `Register`, then let the reference go.

### Unregistering

```csharp
_proxy.Unregister("CCSPlayerPawn", "m_iHealth");           // remove all-entities proxy
_proxy.Unregister(someEntity, "CCSPlayerPawn", "m_iHealth"); // remove per-entity proxy
```

Call `Unregister` in your plugin's `Shutdown()` to clear registrations promptly.

## Example plugin

`YappersHQ.SendProxy.Example` is a working consumer of the interface. All commands are admin-gated
via ModSharp's **AdminManager** and dispatched from chat `!cmd` / `sm_cmd`, the server console, or
RCON. They require the `sendproxy:example` permission (grant it or `*` via your admin source). The
example resolves `AdminManager` in `OnAllModulesLoaded` and retries in `OnLibraryConnected` (so it
survives CommandCenter loading late); AdminManager auto-unregisters the commands on disconnect.

The example is a **test matrix** for the new proxy-field API. Commands registered in the source:

| Command | Demonstrates |
|---|---|
| `sp_proxy <ser> <field> <int\|float\|bool> <value>` | **Uniform** proxy via `Register` + `SetAll` — every client sees the value |
| `sp_proxy_off <ser> <field>` | `Unregister` the all-entities proxy for a field |
| `sp_proxyfor <ser> <field> int <value>` | **Per-viewer** proxy via `Register` + `SetFor` — only the issuer sees the fake value |
| `sp_proxyfor_off <ser> <field>` | `Unregister` the proxy registered by `sp_proxyfor` |
| `sp_fakeaim [target]` | **Per-client + per-entity** showcase — resolve a target via TargetingManager (`@aim`, a nick, `@t`, multi) and fake each target's HP/team/cycling-colour/glow **only to the issuer** |
| `sp_fakeaim_off` | Tear down `sp_fakeaim` + re-dirty so real values go back out |

Examples:
- `sp_proxy CCSPlayerPawn m_iHealth int 1` — every client sees 1 HP on every pawn
- `sp_proxyfor CCSPlayerPawn m_iHealth int 1` — only you see 1 HP
- `sp_fakeaim @aim` — fake HP/team/colour/glow on whoever you're aiming at, only for you
- `sp_proxy_off CCSPlayerPawn m_iHealth` — remove the proxy

## Build / deploy

```bash
./build.sh        # build.bat on Windows
# output under .build/: modules/YappersHQ.SendProxy/, shared/YappersHQ.SendProxy.Shared/, gamedata/
```

Deploy:
- module DLL → `<sharp>/modules/YappersHQ.SendProxy/`
- Shared DLL → `<sharp>/shared/YappersHQ.SendProxy.Shared/`
- example (optional) → `<sharp>/modules/YappersHQ.SendProxy.Example/` (requires the **AdminManager**
  module to be installed — its commands are admin-gated and dispatched through CommandCenter)
- gamedata → `<sharp>/gamedata/yappershq.sendproxy.jsonc`

The gamedata source is `.assets/gamedata/yappershq.sendproxy.jsonc`. `GameData.Register("yappershq.sendproxy")`
loads it from `<sharp>/gamedata/yappershq.sendproxy.jsonc`.

> ⚠️ **`modsharp-deploy` gamedata-nesting gotcha:** the deploy step can nest the asset into a
> `gamedata/gamedata/` subfolder (build copy + deploy copy double up), leaving the file the server
> actually loads at `<sharp>/gamedata/yappershq.sendproxy.jsonc` stale or missing. After deploying,
> write the gamedata **directly** to `/game/sharp/gamedata/yappershq.sendproxy.jsonc` rather than
> trusting the deploy step.

## Lifecycle & safety

Hook lifecycle is managed so a consumer can't dangle a registration and crash the server (mirrors how
SourceMod's SendProxyManager purges hooks on plugin unload / entity removal):

- **Consumer module unload** — every callback records the assembly that registered it. When that
  module disconnects (`OnLibraryDisconnect`), its callbacks are purged automatically, so the send
  path never invokes a delegate into an unloaded `AssemblyLoadContext`. You should still call
  `Unregister` in your own `Shutdown` for promptness, but forgetting to won't crash anything.
- **Entity removal** — entity-scoped registrations are dropped on `OnEntityDeleted` (entity indices
  are reused after disconnect / round restart, so stale scoping can't bleed onto a new entity).
- **Client disconnect** — the per-client path stores **no** per-client state; the recipient is
  captured transiently only for the duration of that client's own encode, so a disconnect needs no
  cleanup.
- **Dispatch is stateless & guarded** — each field is re-resolved per call (no stored entity
  pointers), every native read is `IsUserPtr`-gated, and any field whose encoder isn't a known
  substitutable type classifies as `Unsupported` and passes through untouched. A throwing callback
  is caught → passthrough.

## Status / caveats

- **Per-client substitution and multi-type substitution are mechanism-verified** — recipient capture,
  field-type classification, and the per-field bit-stream rewrite are proven end-to-end across the
  supported types with no output corruption / desync.
- **Top-level fields work** (`m_iHealth`, `m_ArmorValue`, `m_angEyeAngles`, …). Nested-path fields
  (reached through a sub-serializer descent) may not resolve yet — see the RE doc.
- **All signatures are build-specific.** They're located at runtime by pattern (`FindPattern`), so a
  relink that only shifts addresses is fine; a code change to a hooked engine function is not.
  Re-derive on engine updates with **[`docs/FINDING_SIGNATURES.md`](docs/FINDING_SIGNATURES.md)** and the
  offsets/sig notes in **[`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)**.
- **Cross-platform — ⚠️ Windows is UNTESTED LIVE, help wanted.** Linux is fully validated. Every Windows
  signature is in the gamedata, derived by side-by-side decompile vs its Linux twin (the `EncodeEntity`
  capture wrapper's entity-index read at `[rsp+0x28]` is proven end-to-end against `Encode`'s `entity %d`
  log — see the gamedata note), but **no part of the Windows path has been run on a live Windows server.**
  If you can test on Windows, please verify and open an issue with the result:
  1. Plugin loads — boot log shows `EncodeCapture midhook installed @ 0x...` (no "address not resolved").
  2. `sp_proxy CCSPlayerPawn m_iHealth int 1` → all clients see 1 HP, **no crash on player join**.
  3. `sp_proxyfor` → the per-viewer value shows only to the issuer; everyone else sees the real value.
  4. An entity-scoped proxy hits the right entity (sanity-checks the `[rsp+0x28]` entity-index read).
  See `docs/FINDING_SIGNATURES.md` for re-deriving the sigs if a game update breaks them.
- **Live application:** a freshly-registered proxy on an unchanged field applies on the next natural
  re-encode (value change or full update). For entity-scoped registrations, call
  `entity.NetworkStateChanged("fieldName")` immediately after registering to dirty the field and apply
  on the next tick. See `docs/FORCE_RESEND.md` for background on why the delta is value-compared and
  why this is the correct approach.

## Documentation

- **[`docs/HOW_IT_WORKS.md`](docs/HOW_IT_WORKS.md)** — plain-English overview (start here).
- **[`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)** — the full RE / C++-port spec.
- **[`docs/FINDING_SIGNATURES.md`](docs/FINDING_SIGNATURES.md)** — human Ghidra/IDA guide to (re-)deriving every signature, with the Windows-specific gotchas.
- **[`docs/FORCE_RESEND.md`](docs/FORCE_RESEND.md)** — live application background: why the delta is value-compared, and why `NetworkStateChanged` is the correct live-apply mechanism (the earlier force-resend approach was evaluated and not used).
- **[`docs/MERGE_READINESS.md`](docs/MERGE_READINESS.md)** — review notes for bundling into ModSharp core.
- **[`docs/ENCODE_CALLGRAPH_RE.md`](docs/ENCODE_CALLGRAPH_RE.md)** — crash/hook-type analysis for the per-entity encode wrapper hook.

## License & attribution

AGPL-3.0. Signature tooling in `tools/` vendors
[nosoop's `makesig`](https://github.com/nosoop/ghidra_scripts/blob/master/makesig.py)
(attribution kept in `tools/README.md`).
