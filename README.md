# SendProxy for ModSharp (CS2)

SourceMod-`SendProxyManager`-style **per-field network value substitution** for
**ModSharp / Counter-Strike 2 (Source 2)**. Intercept what clients *receive* for a networked
entity field and substitute the value — **without changing the real server-side state**.

A consumer plugin asks the `ISendProxyManager` interface to spoof a field; the library installs the
necessary native detours into CS2's serializer/send path and rewrites the per-client bit-stream as
each snapshot is encoded. Three targeting modes:

- **Uniform** — every client sees the same fake value for the field (`SetUniformInt`).
- **Per-client** — a callback returns a (potentially different) value *per recipient* (`HookInt`/
  `HookFloat`/`HookBool`/`HookVector`).
- **Per-entity** — scope a spoof or callback to a single entity index (`SetUniformIntForEntity`,
  `HookEntityInt`, …).

> **Why this is hard on CS2:** Source 1's SendProxy swapped each `SendProp`'s `m_pProxyFn`, a
> per-field function the engine called *while serializing each client's snapshot* — so per-client
> values were free. Source 2 has none of that: CS2 packs **all** entities **once** into a shared
> `CFrameSnapshot`, then writes each client a delta that **copies pre-encoded bits**. Values are
> global; native per-client differentiation is only *presence* (a recipients bitmask), not *value*.
> This library substitutes per-client by rewriting the per-field bits in the send loop. Full
> analysis: **[`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)**.

## Supported field types

| Category | Types | Status |
|---|---|---|
| Signed integer | int8 / int16 / int32 / int64 | substitutable (zigzag varint) |
| Unsigned integer | uint8 / uint16 / uint32 / uint64 | substitutable (raw varint) |
| Fixed-width integer | fixed32 / fixed64 | substitutable |
| Boolean | bool | substitutable (1 bit) |
| Float | float32 | substitutable (32 raw bits) |
| Angle / vector | qangle, vector3 | substitutable (3 contiguous float32) |
| Float-derived | coord, normal, coord_integral, quantized float | **passthrough** (not yet substitutable) |
| Other | string, array, handle | **passthrough** |

If a field's type can't be classified, the hook passes through untouched — it never writes
wrong-type bits, so an unsupported field is a no-op, not corruption.

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

### Uniform spoof (all clients, all entities)

Every client sees `value` for the field on every entity of that serializer; the real server-side
value is untouched.

```csharp
// Everyone sees 1337 HP on every player pawn; real m_iHealth still drives damage/death.
_sendProxy.SetUniformInt("CCSPlayerPawn", "m_iHealth", 1337);
```

### Per-client callback

The callback runs as each client's snapshot is encoded and returns the value *that client* should
see. Return `false` to leave the original value for that client.

```csharp
_sendProxy.HookInt("CCSPlayerPawn", "m_iHealth", (nint client, int entityIndex, ref int value) =>
{
    value = SomeLookup(client);   // value the recipient sees
    return true;                  // true → substitute, false → leave original
});
```

Typed variants for the other supported categories:

```csharp
_sendProxy.HookFloat("CCSPlayerPawn", "m_flField",
    (nint client, int entityIndex, ref float value) => { value = 0.5f; return true; });

_sendProxy.HookBool("SomeSerializer", "m_bField",
    (nint client, int entityIndex, ref bool value) => { value = true; return true; });

// QAngle3 / Vector3 — three contiguous float32 (e.g. eye angles).
_sendProxy.HookVector("CCSPlayerPawn", "m_angEyeAngles",
    (nint client, int entityIndex, ref float x, ref float y, ref float z) =>
    { x = 0f; y = 90f; z = 0f; return true; });
```

#### Callback contract — read before writing one

- **Runs on the engine's send worker threads** (~6 of them). The callback **must be thread-safe and
  fast** — no locks held long, no blocking, no allocation on the hot path. Capture immutable data by
  value or index into a pre-sized slot array.
- **`client`** is the raw `CServerSideClient*` (an `nint`) for the recipient. Use it as an opaque
  key / pointer; don't assume it can be safely dereferenced from managed code.
- **`entityIndex`** is the entity currently being sent (`-1` if it wasn't captured).
- **Return `false`** to leave the original (real) value for that client; **`true`** to substitute the
  value you wrote by `ref`.
- A throwing callback is caught and treated as passthrough for that field/client.

### Per-entity targeting

Scope a spoof or callback to one entity index. When both a per-entity and a global (`-1`)
registration exist for the same `(serializer, field)`, the per-entity one wins for that entity.

```csharp
// Only entity 42's pawn shows 1 HP to everyone; other pawns unaffected.
_sendProxy.SetUniformIntForEntity(42, "CCSPlayerPawn", "m_iHealth", 1);

// Per-entity per-client callback.
_sendProxy.HookEntityInt(42, "CCSPlayerPawn", "m_iHealth",
    (nint client, int entityIndex, ref int value) => { value = 1; return true; });
// Also: HookEntityFloat / HookEntityBool / HookEntityVector, and SetEntitySpoof.
```

### Unhooking

```csharp
_sendProxy.UnhookInt("CCSPlayerPawn", "m_iHealth");      // remove global callback for a field (type-specific name)
_sendProxy.Unhook("CCSPlayerPawn", "m_angEyeAngles");    // type-agnostic: remove global callback for a field
_sendProxy.UnhookEntity(42, "CCSPlayerPawn", "m_iHealth"); // remove the per-entity registration only
_sendProxy.UnhookAllPerClient();                          // remove EVERY registration + uninstall detours
```

Call `UnhookAllPerClient()` in your plugin's `Shutdown()` if you registered any callbacks — it clears
all global/per-entity registrations and uninstalls the native detours, avoiding dangling delegate
references.

## Example plugin

`YappersHQ.SendProxy.Example` is a working consumer of the interface. It registers these server
commands (each exercises one part of the API):

| Command | Demonstrates |
|---|---|
| `sp_example_fakehp <value>` | **Uniform** spoof — `SetUniformInt` on `CCSPlayerPawn::m_iHealth`, all clients, all pawns |
| `sp_example_fakehp_entity <entityIndex> <fakeValue>` | **Per-entity** uniform spoof — `SetEntitySpoof` scoped to one entity |
| `sp_example_perclienthp <baseValue>` | **Per-client** int callback — `HookInt`, derives a per-recipient value from the client pointer |
| `sp_example_perclienthp_off` | `UnhookInt` — remove that per-client callback |
| `sp_example_fakeangle <pitch> <yaw> <roll>` | **Vector/QAngle** callback — `HookVector` on `m_angEyeAngles`, fixed angles for all clients |
| `sp_example_fakeangle_off` | `Unhook` — remove the eye-angle callback |
| `sp_example_off` | `UnhookAllPerClient` — clear everything, uninstall Phase-2 detours |

## Build / deploy

```bash
./build.sh        # build.bat on Windows
# output under .build/: modules/YappersHQ.SendProxy/, shared/YappersHQ.SendProxy.Shared/, gamedata/
```

Deploy:
- module DLL → `<sharp>/modules/YappersHQ.SendProxy/`
- Shared DLL → `<sharp>/shared/YappersHQ.SendProxy.Shared/`
- example (optional) → `<sharp>/modules/YappersHQ.SendProxy.Example/`
- gamedata → `<sharp>/gamedata/yappershq.sendproxy.jsonc`

The gamedata source is `.assets/gamedata/yappershq.sendproxy.jsonc`. `GameData.Register("yappershq.sendproxy")`
loads it from `<sharp>/gamedata/yappershq.sendproxy.jsonc`.

> ⚠️ **`modsharp-deploy` gamedata-nesting gotcha:** the deploy step can nest the asset into a
> `gamedata/gamedata/` subfolder (build copy + deploy copy double up), leaving the file the server
> actually loads at `<sharp>/gamedata/yappershq.sendproxy.jsonc` stale or missing. After deploying,
> write the gamedata **directly** to `/game/sharp/gamedata/yappershq.sendproxy.jsonc` rather than
> trusting the deploy step.

## Status / caveats

- **Per-client substitution and multi-type substitution are mechanism-verified** — recipient capture,
  field-type classification, and the per-field bit-stream rewrite are proven end-to-end across the
  supported types with no output corruption / desync.
- **Top-level fields work** (`m_iHealth`, `m_ArmorValue`, `m_angEyeAngles`, …). Nested-path fields
  (reached through a sub-serializer descent) may not resolve yet — see the RE doc.
- **All signatures are build-specific.** They're located at runtime by pattern (`FindPattern`), so a
  relink that only shifts addresses is fine; a code change to a hooked engine function is not.
  Re-derive on engine updates with the tooling in `tools/` and the offsets/sig notes in
  **[`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)**.

## License & attribution

AGPL-3.0. Signature tooling in `tools/` vendors
[nosoop's `makesig`](https://github.com/nosoop/ghidra_scripts/blob/master/makesig.py)
(attribution kept in `tools/README.md`).
</content>
</invoke>
