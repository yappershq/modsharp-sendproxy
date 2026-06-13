# SendProxy for ModSharp (CS2)

A port of [SourceMod's SendProxy / SendProxyManager](https://github.com/KissLick/SendProxyManager)
to **ModSharp / Counter-Strike 2 (Source 2)**: intercept the serialization of networked entity
fields and substitute the value sent to clients, **without changing the real server-side state**.

> **Status: early.** The public API and architecture are stable and the module compiles + loads.
> The live per-field encoder patch is **gated off** (`EncoderHook.Enabled = false`) until the
> reverse-engineered offsets are verified against your server build — see
> [Phase 1 — remaining](#phase-1--remaining). Registration works today; value substitution is the
> next step.

## Why this is hard on CS2 (the RE)

SourceMod's SendProxy swaps a `SendProp`'s `m_pProxyFn` — a per-field function the Source 1 engine
calls **while serializing each client's snapshot**, so a plugin can return a different value per
client. Source 2 has none of that machinery. What we found reverse-engineering the CS2 server
binaries (`libnetworksystem.so`, `libengine2.so`):

### Networking model
- Source 1 `SendTable`/`SendProp` is replaced by **`CFlattenedSerializer`** + `CSerializedEntities`
  (schema-driven). Per-field metadata lives in **`CNetworkSerializerFieldInfo`** (the SendProp
  analog), which carries named encoders (`m_NetworkSerializer`/`m_NetworkEncoder`), a per-field
  **send-proxy recipients filter** (`m_NetworkSendProxyRecipientsFilter` →
  `void(*)(CEntityInstance*, CCheckTransmitInfo*, CPlayerBitVec&)`), and the field offset.
- The server **packs all entities once** into a shared `CFrameSnapshot`
  (`CNetworkGameServer::PackEntities`, threaded), then writes a **per-client delta**
  (`WriteDeltaEntity_Internal`) gated by a recipients bitmask. **Values are encoded once and shared;
  per-client differentiation is presence (mask), not value.**

### The hook point (the `m_pProxyFn` analog)
Inside `CFlattenedSerializer::EncodeField`, the per-field value write is an indirect call:
```
(*(code*)**(void***)(fieldInfo + 0x38))(bf_write* buf, fieldInfo, void* valuePtr, void* ctx, uint unk)
```
i.e. **vtable slot 0 of the encoder dispatch object at `CNetworkSerializerFieldInfo + 0x38`**, where
`valuePtr` is the value's address in entity memory. Swapping that per field — only for hooked fields
— lets us read the real value, run a callback, and encode a substituted value. Untouched fields keep
their native pointer, so the other ~10k entities cost **zero**.

### Feasibility
| Capability | Verdict |
|---|---|
| Uniform value spoof (same to all clients) | ✅ per-field encoder swap — cheap, faithful to SM |
| Per-client **presence** (hide a field from a client) | ✅ native via the recipients filter |
| Per-client **different value** (full matrix) | 🟡 CS2 encodes once; requires surgical per-client override at the send loop (`SendClientMessages` +0x80, current client in scope) for hooked entities only — avoid the naive "re-pack per client" which is `pack × N` and kills perf at 64p/10k |

## Architecture
- **`YappersHQ.SendProxy.Shared`** — the public API (`ISendProxyManager`), mirrors `sendproxy.inc`,
  plus an `IGameClient?` recipient argument for the per-client matrix.
- **`YappersHQ.SendProxy`** — the module: lifecycle, the hook registry, and the encoder swap
  (gated). `Native/FlattenedSerializerLayout.cs` holds the RE'd offsets;
  `.asset/gamedata/yappershq.sendproxy.games.jsonc` documents the targets.

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

## Build
```bash
./build.sh        # or build.bat on Windows
# output: .build/modules/YappersHQ.SendProxy/ (+ .build/shared/..., + gamedata under .build/)
```
Deploy the module DLL to `<sharp>/modules/YappersHQ.SendProxy/`, the Shared DLL to
`<sharp>/shared/YappersHQ.SendProxy.Shared/`, and the gamedata to `<sharp>/gamedata/`.

## Phase 1 — remaining
1. Verify `CNetworkSerializerFieldInfo` offsets (esp. `+0x38` encoder dispatch, `+0x40` value) on the
   target build; extract + verify the `EncodeField` signature into the gamedata file.
2. Implement `SendProxyManager.ApplyEncoderSwap`: resolve the field info for `(entity-class, prop)`,
   install a trampoline (`[UnmanagedCallersOnly]`) on the encoder dispatch slot, invoke the callback,
   chain to the original encoder.
3. Flip `EncoderHook.Enabled` and test uniform value spoof on a live server.

## Phase 2 — per-client
- Per-client **presence** via the recipients filter (`+0x28`).
- Per-client **value**: set a `thread_local` current-client at the per-client send (`SendClientMessages`
  +0x80) and override only hooked entities' hooked fields in that client's stream (cost = hooked ×
  clients, not all × clients).

## Reverse-engineering notes
Binaries: CS2 dedicated-server `libnetworksystem.so` / `libengine2.so` (build 2026-06-02), stripped.
Targets located via assert-string xrefs + RTTI in Ghidra. Struct layout cross-checked against the
[hl2sdk `cs2` branch](https://github.com/alliedmodders/hl2sdk/tree/cs2)
(`public/networksystem/inetworkserializer.h`). Offsets are build-specific — re-verify on update.
