# SendProxy for ModSharp (CS2)

A port of [SourceMod's SendProxy / SendProxyManager](https://github.com/KissLick/SendProxyManager)
to **ModSharp / Counter-Strike 2 (Source 2)**: intercept the serialization of networked entity
fields and substitute the value sent to clients, **without changing the real server-side state**.

## What it does

- **Phase 1 — uniform value spoof:** substitute a networked field's value so **all clients** see the
  fake, while the real server-side value is never touched. This is the faithful SourceMod-style send
  proxy. **Working and live-tested** — e.g. `sp_fakehp 1337` makes every client see 1337 HP while a
  real `ms_slap` still applies real damage to the true (untouched) server-side `m_iHealth`.
- **Phase 2 — per-client value substitution:** different clients can be shown **different values** for
  the same field, still without changing server-side state. The per-client substitution pipeline is
  **proven end-to-end in verify mode** (the recipient client is captured, `m_iHealth` resolves, the
  bit-stream cursor math is correct, and there is **zero output corruption / no desync**); the final
  live fake-value confirmation is still pending.

> **Why this is hard on CS2:** Source 1's SendProxy swapped each `SendProp`'s `m_pProxyFn` — a
> per-field function the engine called **while serializing each client's snapshot**, so a plugin could
> return a different value per client for free. Source 2 has none of that. CS2 packs **all** entities
> **once** into a shared `CFrameSnapshot`, then writes a per-client delta that **copies pre-encoded
> bits** — values are global, and per-client differentiation is natively only *presence* (a recipients
> bitmask), not *value*. Phase 1 hooks the single shared encode; Phase 2 substitutes per-client at the
> per-field bit-copy in the send loop. Full analysis: **[`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)**.

## Status

| Capability | State |
|---|---|
| Phase 1 — uniform value spoof (all clients see the fake) | ✅ **Working, live-tested** (`sp_fakehp`) |
| Phase 2 — per-client value substitution | 🟡 **Pipeline proven in verify mode** — recipient captured, field resolves, cursor math correct, zero corruption. Live fake confirmation pending. |
| Per-client *presence* (hide a field from a client) | native via the recipients filter (not yet wired) |

**Known limits (current):**
- **int32 fields only** so far — the working encoder hook targets the integer encoder (`m_iHealth`,
  `m_ArmorValue`, score/account/tick fields, etc.). Float/string/vector encoders are catalogued in the
  RE doc but not yet wired.
- **Top-level fields work** (`m_iHealth`, `m_ArmorValue`). **Nested-path fields** (fields reached
  through a sub-serializer descent) still resolve empty — the CFieldPath descent needs a fix before
  nested fields can be substituted in Phase 2.

## Commands

> These are **test / diagnostic commands** living in the core module for development. They are slated
> to move into the Example plugin — they are not the public consumer API (which is `ISendProxyManager`,
> below).

**Phase 1 (uniform spoof):**
- `sp_fakehp <value>` — spoof `m_iHealth` for all clients (real HP stays real). Installs the int32
  encoder detour on first use.
- `sp_fakehp_off` — stop spoofing `m_iHealth`; uninstalls the detour when no spoofs remain.

**Phase 2 (per-client substitution):**
- `sp_sub_verify` — install the three substitution sub-detours + register `CCSPlayerPawn::m_iHealth` in
  **Verify** mode (read-only: logs cursor math + resolved field identity, writes nothing).
- `sp_fakehp2 <value>` — **Fake** mode: register `CCSPlayerPawn::m_iHealth → value` and substitute per
  client in the send loop.
- `sp_sub_off` — disable substitution, clear the registry, uninstall the sub-detours.

**Read-only diagnostics / RE probes:**
- `sp_dump [entityIndex]` — dump an entity's network serializer layout (no arg = scan for live entities).
- `sp_field <class> <fieldName>` — dump a serializer field record, e.g. `sp_field CCSPlayerPawn m_iHealth`.
- `sp_encprobe` / `sp_encprobe_off` — int32-encoder probe; logs which arg register carries the value.
- `sp_recipcap` / `sp_recipcap_off` — per-client-encode probe; captures the recipient `CServerSideClient*`.
- `sp_wflprobe` / `sp_wflprobe_off` — `WriteFieldList` verify probe (client + serializer + field identity).
- `sp_wdeprobe` / `sp_wdeprobe_off` — `WriteDeltaEntity_Internal` probe (thread id + ctx field offsets).
- `sp_detour_on force` / `sp_detour_off` — **unsafe** `EncodeField`-entry probe. Gated behind `force`
  because detouring `EncodeField`'s entry crashes the server (its prologue reads stack args the 6-arg
  passthrough doesn't preserve). The real value hook is the per-field encoder, not `EncodeField`'s entry.

## Architecture
- **`YappersHQ.SendProxy.Shared`** — the public API (`ISendProxyManager`), mirrors `sendproxy.inc`,
  plus an `IGameClient?` recipient argument for the per-client matrix.
- **`YappersHQ.SendProxy`** — the module: lifecycle, the hook registry, and the native detours.
  - `Native/IntEncoderDetour.cs` — Phase 1 int32 encoder detour (uniform spoof).
  - `Native/FieldSubstitution.cs` — Phase 2 per-client per-field substitution (WFL-level bit-stream rewrite).
  - `Native/RecipientCapture.cs` — captures the per-client recipient into a `[ThreadStatic]`.
  - `Native/WriteFieldProbe.cs`, `WriteDeltaProbe.cs`, `EncodeFieldDetour.cs`, `SerializerProbe.cs` — RE probes.
  - `.assets/gamedata/yappershq.sendproxy.jsonc` — the RE'd sigs/offsets (see below).

## Usage (consumer plugin)
```csharp
var sp = sharpModuleManager
    .GetOptionalSharpModuleInterface<ISendProxyManager>(ISendProxyManager.Identity)?.Instance;

// Show every client 1 HP on the HUD while the real m_iHealth is unchanged:
sp.HookInt(playerPawnEntityIndex, "m_iHealth", (client, entity, prop, element, ref value) =>
{
    value = 1;
    return SendProxyResult.Changed;
});
```

## Build / deploy
```bash
./build.sh        # or build.bat on Windows
# output: .build/modules/YappersHQ.SendProxy/ (+ .build/shared/..., + gamedata under .build/)
```
Deploy the module DLL to `<sharp>/modules/YappersHQ.SendProxy/`, the Shared DLL to
`<sharp>/shared/YappersHQ.SendProxy.Shared/`, and the gamedata to `<sharp>/gamedata/`.

The gamedata is `.assets/gamedata/yappershq.sendproxy.jsonc` (9 sig entries: `EncodeField`,
`EncodeInt32`, `SendClientMessages`, `PerClientEncode`, `WriteDeltaEntity_Internal`, `WriteFieldList`,
`GetBitRange`, `BitCopyPrimitive`, `VarintWriter`). `GameData.Register("yappershq.sendproxy")` loads
`<sharp>/gamedata/yappershq.sendproxy.jsonc`.

> ⚠️ **Deploy gotcha:** `modsharp-deploy` nests the asset gamedata into a `gamedata/gamedata/`
> subfolder (the build copy + the deploy copy double up), so the file the server actually loads at
> `<sharp>/gamedata/yappershq.sendproxy.jsonc` can end up stale/missing. After deploying, write the
> gamedata **directly** to `/game/sharp/gamedata/yappershq.sendproxy.jsonc` rather than relying on the
> deploy step.

All sigs are **build-specific** — re-derive on engine updates with the tooling in `tools/`
(nosoop's `makesig`, plus a headless Java port). Sigs are position-independent (`FindPattern` resolves
them at runtime), so a relink that only shifts addresses doesn't break them; a code change to a hooked
function does.

## Reverse-engineering notes
**Full RE writeup: [`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)** — the complete CS2
networking analysis: the shared-pack pipeline, the Phase-1 encoder hook point, the Phase-2 per-client
send path + substitution mechanism, struct layouts, game-function resolution, and the offsets table.

Binaries: CS2 dedicated-server `libnetworksystem.so` (serializer) / `libengine2.so` (send loop),
stripped. Targets located via log-string xrefs + RTTI in Ghidra; offsets are file-vaddr RVAs (Ghidra
image base = file-vaddr + 0x100000). Offsets are build-specific — re-verify on update.

## License & attribution
AGPL-3.0. Signature tooling in `tools/` vendors [nosoop's `makesig`](https://github.com/nosoop/ghidra_scripts/blob/master/makesig.py)
(attribution kept in `tools/README.md`).
