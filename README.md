# SendProxy for ModSharp (CS2)

**Per-field network value substitution** for **ModSharp / Counter-Strike 2 (Source 2)**. Intercept
what clients *receive* for a networked entity field and substitute the value — **without changing the
real server-side state**. (SourceMod's `SendProxyManager` was a conceptual reference only; the API is
ModSharp-native — see the unified `SpoofValue` API below.)

A consumer plugin asks the `ISendProxyManager` interface to spoof a field; the library installs the
necessary native detours into CS2's serializer/send path and rewrites the per-client bit-stream as
each snapshot is encoded. Three targeting modes:

- **Uniform** — every client sees the same fake value for the field (`SetUniform`).
- **Per-client** — one `SendProxyCallback` returns a (potentially different) value *per recipient*
  (`Hook`), reading/writing a tagged `SpoofValue`.
- **Per-entity** — scope a spoof or callback to a single `IBaseEntity` (`SetUniform(entity, ...)` /
  `Hook(entity, ...)`).

Values flow through one `SpoofValue` type (factories `SpoofValue.Int/Float/Bool/Vector/String/Bytes`;
typed `v.AsInt`/`v.AsFloat`/… accessors in callbacks) — a single API surface for every field type.

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

## Consumer API — `ISendProxyManager`

### Resolving the interface

`ISendProxyManager` is published in the library's `PostInit`. ModSharp does **not** order
`PostInit`/`OnAllModulesLoaded` across plugins, but it does guarantee every plugin's `PostInit`
finishes before any `OnAllModulesLoaded` fires — so resolve the interface in **`OnAllModulesLoaded`**,
not `Init`/`PostInit`. Cache the reference for the plugin's lifetime.

```csharp
private ISendProxyManager? _sendProxy;

public void OnAllModulesLoaded()
{
    _sendProxy = _modules
        .GetOptionalSharpModuleInterface<ISendProxyManager>(ISendProxyManager.Identity)?.Instance;

    if (_sendProxy is null)
        _logger.LogWarning("SendProxy not loaded — feature disabled");
}
```

### Method surface

All registration is through two overload families:

- `Hook(serializerName, fieldName, callback)` — per-client callback, all entities of that serializer.
- `Hook(entity, serializerName, fieldName, callback)` — per-client callback, scoped to one `IBaseEntity`.
- `SetUniform(serializerName, fieldName, value)` — same value for every client, all entities.
- `SetUniform(entity, serializerName, fieldName, value)` — same value for every client, single entity.
- `Unhook(serializerName, fieldName)` — remove all-entities registration.
- `Unhook(entity, serializerName, fieldName)` — remove single-entity registration.
- `UnhookAll()` — remove every registration and uninstall the substitution detours.
- `IsHooked(serializerName, fieldName)` — returns true if any registration exists for that field.

There's a single callback delegate — `SendProxyCallback(nint client, int entityIndex, ref SpoofValue value)`
— and a single value type, `SpoofValue`, used by both `Hook` (callback) and `SetUniform`/`SendFake`
(fixed value). Build a value with `SpoofValue.Int(…)` / `.Float(…)` / `.Bool(…)` / `.Vector(…)` /
`.String(…)` / `.Bytes(…)`; inside a callback, read/write `value.AsInt` / `.AsFloat` / `.AsBool` /
`.AsVector` / `.AsString` / `.AsBytes`.

`SendFake(client, entity, serializerName, fieldName, in SpoofValue)` pushes a one-shot fake to a single
client. Per-entity registrations win over all-entities registrations for that entity when both exist.
Detours install lazily on first registration. `SetForceResend(bool)` toggles live application (see below).

### Uniform spoof (all clients, all entities)

```csharp
// Everyone sees 1337 HP on every player pawn; real m_iHealth still drives damage/death.
_sendProxy.SetUniform("CCSPlayerPawn", "m_iHealth", SpoofValue.Int(1337));

// Force a string field to a fixed value for all clients.
_sendProxy.SetUniform("CCSGameRulesProxy", "m_szMatchStatTxt", SpoofValue.String("custom text"));
```

### Per-client callback

The callback runs as each client's snapshot is encoded; return `true` to substitute the value you
wrote via `ref`, `false` to leave the original.

```csharp
// Per-client int — each recipient sees a value derived from their client pointer.
_sendProxy.Hook("CCSPlayerPawn", "m_iHealth",
    (nint client, int entityIndex, ref SpoofValue value) =>
    {
        value.AsInt = SomeLookup(client);
        return true;
    });

// Per-client string field.
_sendProxy.Hook("SomeSerializer", "m_szSomeField",
    (nint client, int entityIndex, ref SpoofValue value) =>
    {
        value.AsString = GetStringForClient(client);
        return true;
    });
```

#### Callback contract — read before writing one

- **Runs on the engine's send worker threads** (~6 of them). The callback **must be thread-safe and
  fast** — no locks held long, no blocking, no allocation on the hot path. Capture immutable data by
  value or index into a pre-sized slot array.
- **`client`** is the raw `CServerSideClient*` (`nint`) for the recipient. Use as an opaque key;
  do not dereference from managed code.
- **`entityIndex`** is the entity currently being sent (`-1` if not captured).
- **Return `false`** to leave the original value for that client; **`true`** to substitute the value
  you wrote into the `ref SpoofValue` (via `value.AsInt`/`.AsFloat`/…). The `SpoofValue` is seeded
  with the registered uniform value, or the type zero/empty if none is registered.
- A throwing callback is caught and treated as passthrough for that field/client.

### Per-entity targeting

```csharp
// All clients see 1 HP for one specific entity; other pawns unaffected.
_sendProxy.SetUniform(someEntity, "CCSPlayerPawn", "m_iHealth", SpoofValue.Int(1));

// Per-client callback scoped to one entity.
_sendProxy.Hook(someEntity, "CCSPlayerPawn", "m_iHealth",
    (nint client, int entityIndex, ref SpoofValue value) => { value.AsInt = 1; return true; });
```

`someEntity` is an `IBaseEntity` reference resolved at registration time. Do not store `IBaseEntity`
long-term — resolve it fresh, call the registration once, then let the reference go.

### Unhooking

```csharp
_sendProxy.Unhook("CCSPlayerPawn", "m_iHealth");           // remove all-entities registration
_sendProxy.Unhook(someEntity, "CCSPlayerPawn", "m_iHealth"); // remove single-entity registration
_sendProxy.UnhookAll();                                     // remove everything + uninstall detours
```

Call `UnhookAll()` in your plugin's `Shutdown()` — it clears all registrations and uninstalls the
native detours, avoiding dangling delegate references.

## Example plugin

`YappersHQ.SendProxy.Example` is a working consumer of the interface. The core library itself ships
**no commands** — all demo/diagnostic commands live in the example and are registered through ModSharp's
**AdminManager** (admin-gated, dispatched from chat `!cmd` / `sm_cmd`, the server console and RCON).
They require the `sendproxy:example` permission; grant it (or `*`) via your admin source. The example
resolves AdminManager in `OnAllModulesLoaded` and retries in `OnLibraryConnected` (so it survives
CommandCenter loading late); AdminManager auto-unregisters the commands on disconnect.

It is a **test matrix**: the generic commands hit any encoder type, on any field, in any mode, with no
recompile. `<type>` is one of `int uint float bool vec string bytes` (mapping: `int`→bucket 1, `uint`→2,
`vec`→3 qangle/vector/coord/quantized, `float`→4, `string`→5, `bytes`→6, `bool`→7). `vec` takes three
floats; `string` takes free text; `bytes` takes a contiguous hex string (e.g. `DEADBEEF`).

| Command | Demonstrates |
|---|---|
| `sp_set <ser> <field> <type> <value...>` | **Uniform** spoof (all clients) — dispatches to the matching `SetUniform` overload |
| `sp_setpc <ser> <field> <type>` | **Per-client** callback — registers a `Hook(...)` proxy whose value varies per recipient |
| `sp_setent <idx> <ser> <field> <type> <value...>` | **Per-entity** uniform spoof — `SetUniform(entity, ...)` scoped to one entity |
| `sp_unset <ser> <field>` | `Unhook` the all-entities registration for a field |
| `sp_clear` | `UnhookAll` — clear everything, uninstall the substitution detours |
| `sp_help` | Print the matrix + ready-to-paste examples |
| `sp_sendfake <value>` | **SendFake** one-shot — push a fake HP to the issuer once (force-dirty + fire once), no persistent hook |
| `sp_fakeaim <target>` | **Per-client + per-entity** showcase — resolve a target string via TargetingManager (`@aim`, a nick, `@t`, multi) and fake each target's HP/team/cycling-colour/glow **only to the issuer** |
| `sp_fakeaim_off` | Tear down `sp_fakeaim` + re-send the real values |
| `sp_fakehp <value>` | Preset — `SetUniform("CCSPlayerPawn","m_iHealth", value)` |
| `sp_fakename <text>` | Preset — `SetUniform("CCSPlayerController","m_iszPlayerName", text)` (b5 string) |
| `sp_encoder1`..`sp_encoder7` | One canned visible demo per encoder bucket — 1 int `m_iPawnHealth`+`m_iHealth`=1337, 2 uint `m_iTeamNum` (pawn+controller — all appear CT), 3 qangle `m_angEyeAngles` (spectate a target to see), 4 float `m_flScale`=0.3 (tiny players), 5 string `m_iszPlayerName`, 6 bytes (none common — guidance), 7 bool `m_bIsScoped`. Each force-resends so it shows at once |
| `sp_encoders_off` | Revert all `sp_encoder1..7` demos (re-sends real values) |
| `sp_probe_scan` | Read-only serializer probe — list live entities + classes, dump the first |
| `sp_probe_dump <entityIndex>` | Read-only — dump one entity's serializer class info / field[0] |
| `sp_probe_field <serializerClass> <fieldName>` | Read-only — dump a field record's qword window (RE aid) |

Examples: `sp_set CCSPlayerPawn m_iHealth int 1337` · `sp_set CCSPlayerController m_iszPlayerName string Hacker`
· `sp_setpc CCSPlayerPawn m_iHealth int` (each client a different HP) · `sp_setent 3 CCSPlayerPawn m_iHealth int 1`.

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

- **Consumer module unload** — every per-client callback records the assembly that registered it. When
  that module disconnects (`OnLibraryDisconnect`), its callbacks are purged automatically, so the send
  path never invokes a delegate into an unloaded `AssemblyLoadContext`. You should still call `Unhook`
  in your own `Shutdown` for promptness, but forgetting to won't crash anything.
- **Entity removal** — entity-scoped registrations are dropped on `OnEntityDeleted` (entity indices are
  reused after disconnect / round restart, so stale scoping can't bleed onto a new entity).
- **Client disconnect** — the per-client path stores **no** per-client state; the recipient is captured
  transiently (`[ThreadStatic]`) only for the duration of that client's own encode, so a disconnect
  needs no cleanup.
- **Dispatch is stateless & guarded** — each field is re-resolved per call (no stored entity pointers),
  every native read is `IsUserPtr`-gated, and any field whose encoder isn't a known substitutable type
  classifies as `Unsupported` and passes through untouched. A throwing callback is caught → passthrough.

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
- **Cross-platform:** Linux is the validated platform. Windows signatures are in the gamedata (each
  verified by side-by-side decompile vs its Linux twin) but the Windows substitution path awaits live
  validation on a Windows server — see `docs/FORCE_RESEND.md` / `docs/FINDING_SIGNATURES.md`.
- **Live application:** a freshly-registered spoof on an unchanged field applies on the next full update
  by default; `SetForceResend(true)` makes it apply live (gated off — see `docs/FORCE_RESEND.md`).

## Documentation

- **[`docs/HOW_IT_WORKS.md`](docs/HOW_IT_WORKS.md)** — plain-English overview (start here).
- **[`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)** — the full RE / C++-port spec.
- **[`docs/FINDING_SIGNATURES.md`](docs/FINDING_SIGNATURES.md)** — human Ghidra/IDA guide to (re-)deriving every signature, with the Windows-specific gotchas.
- **[`docs/FORCE_RESEND.md`](docs/FORCE_RESEND.md)** — live application of a per-client spoof (why the delta is value-compared, and the gated `SetForceResend` design).
- **[`docs/MERGE_READINESS.md`](docs/MERGE_READINESS.md)** — review notes for bundling into ModSharp core.

## License & attribution

AGPL-3.0. Signature tooling in `tools/` vendors
[nosoop's `makesig`](https://github.com/nosoop/ghidra_scripts/blob/master/makesig.py)
(attribution kept in `tools/README.md`).
</content>
</invoke>
