<div align="center">
  <h1><strong>SendProxy</strong></h1>
  <p>Per-field network value spoofing for ModSharp / CS2 — change what clients receive for a networked field without touching real server state.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/license/yappershq/modsharp-sendproxy" alt="License">
  <img src="https://img.shields.io/github/stars/yappershq/modsharp-sendproxy?style=flat&logo=github" alt="Stars">
</p>

---

SendProxy is a ModSharp port of SourceMod's **SendProxy**, rebuilt for CS2 (Source 2). It lets other plugins register a *proxy* on a networked field — addressed by `(serializerName, fieldName)`, e.g. `("CCSPlayerPawn", "m_iHealth")` — and decide, per packed entity, the value clients receive for that field. The real server-side value is never mutated. Spoofs can be uniform (every client) or per-viewer (specific recipients only). The callback fires **once per (entity, field)** during the shared pack pass, so cost is O(1) in player count.

This repo is library + example. The core `SendProxy` module ships the engine integration and the `IProxyManager` API; the optional `SendProxy.Example` module is a reference consumer with admin commands to test the mechanism live.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/YappersHQ.SendProxy/` | `<sharp>/modules/YappersHQ.SendProxy/` |
| `.build/shared/YappersHQ.SendProxy.Shared/` | `<sharp>/shared/YappersHQ.SendProxy.Shared/` |
| `.build/gamedata/yappershq.sendproxy.jsonc` | `<sharp>/gamedata/yappershq.sendproxy.jsonc` |
| `.build/modules/YappersHQ.SendProxy.Example/` *(optional)* | `<sharp>/modules/YappersHQ.SendProxy.Example/` |

Restart the server (or change map) to load. The gamedata file is required — it carries the signatures SendProxy resolves at boot (re-derive on engine updates with the helpers in `tools/`). Install `SendProxy.Example` only if you want the demo commands.

## ⌨️ Commands

All commands below belong to the **optional `SendProxy.Example`** module (the core library ships no commands). They are admin-gated on the `sendproxy:example` permission, which the example module registers on load.

| Command | Description | Permission |
|---------|-------------|------------|
| `sp_proxy <serializer> <field> <type> <value>` | Register a uniform proxy — every client sees the faked value | `sendproxy:example` |
| `sp_proxy_off <serializer> <field>` | Remove the uniform proxy for a field | `sendproxy:example` |
| `sp_proxyfor <serializer> <field> int <value>` | Per-viewer proxy — only you (the issuer) see the faked value | `sendproxy:example` |
| `sp_proxyfor_off <serializer> <field>` | Remove the per-viewer proxy | `sendproxy:example` |
| `sp_fakeaim` | Per-viewer HP / team / cycling-color spoof on resolved targets | `sendproxy:example` |
| `sp_fakeaim_off` | Clear the fake-aim spoof | `sendproxy:example` |
| `sp_probe_scan` | Read-only: scan live entities and list serializers (logs to server) | `sendproxy:example` |
| `sp_probe_dump <entityIndex>` | Dump one entity's fields + encoder types | `sendproxy:example` |
| `sp_probe_field <serializer> <field>` | Probe a single field's encoder classification | `sendproxy:example` |

## 🔧 How it works

SendProxy hooks the CS2 flattened-serializer **encode path**: it captures the per-entity encode wrapper for entity/serializer context and detours the per-field encoder functions so a registered proxy can substitute the value written into the snapshot. Uniform spoofs (`SetAll`) go into the shared snapshot and ride each client's delta naturally; per-viewer spoofs (`SetFor`) are applied at the per-client write via a recipient-capture hook. Native targets are resolved from `gamedata/yappershq.sendproxy.jsonc` at boot (Linux + Windows signatures), so there are no hard-coded offsets.

For the full mechanism and the CS2 reverse-engineering writeup:

| Doc | Topic |
|-----|-------|
| [docs/HOW_IT_WORKS.md](docs/HOW_IT_WORKS.md) | End-to-end mechanism overview |
| [docs/REVERSE_ENGINEERING.md](docs/REVERSE_ENGINEERING.md) | Full RE derivation of the encode/send path |
| [docs/ENCODE_CALLGRAPH_RE.md](docs/ENCODE_CALLGRAPH_RE.md) | Encode call-graph + why the wrapper is the safe capture site |
| [docs/RE_ENCODERS.md](docs/RE_ENCODERS.md) | Encoder registry / bucket classification |
| [docs/RE_PERCLIENT_EMIT.md](docs/RE_PERCLIENT_EMIT.md) | Per-client emit path (recipient capture) |
| [docs/FINDING_SIGNATURES.md](docs/FINDING_SIGNATURES.md) | How to (re-)derive the gamedata signatures |
| [docs/FORCE_RESEND.md](docs/FORCE_RESEND.md) | Forcing a re-send of changed fields |

## 🧩 Public API

Other plugins consume `IProxyManager` (resolve in `OnAllModulesLoaded`):

```csharp
var proxy = sharpModuleManager
    .GetOptionalSharpModuleInterface<IProxyManager>(IProxyManager.Identity)?.Instance;

// Every client sees 1 HP on every CCSPlayerPawn:
proxy.Register("CCSPlayerPawn", "m_iHealth",
    (ref ProxyContext ctx) => ctx.SetAll(SpoofValue.Int(1)));

// Only one viewer sees a different value:
proxy.Register("CCSPlayerPawn", "m_iHealth",
    (ref ProxyContext ctx) => ctx.SetFor(viewerClient, SpoofValue.Int(100)));
```

- `IProxyManager` — `Register` / `Unregister` (all-entities or single-entity scope) + `IsRegistered`.
- `ProxyContext` — the callback argument: `Entity`, `Field`, `Original`, and the `SetAll` / `SetFor` outputs.
- `SpoofValue` — typed value factories: `Int`, `Float`, `Bool`, `Vector`, `String`, `Bytes`.

Callbacks run on a pack worker thread and must not call main-thread-only engine APIs — read schema / compute values only.

## 📦 Build

```bash
./build.sh    # or: dotnet build src/YappersHQ.SendProxy/YappersHQ.SendProxy.csproj -c Release
```

Outputs to `.build/`:

| Path | Artifact |
|------|----------|
| `.build/modules/YappersHQ.SendProxy/YappersHQ.SendProxy.dll` | core module |
| `.build/shared/YappersHQ.SendProxy.Shared/YappersHQ.SendProxy.Shared.dll` | public API contract |
| `.build/modules/YappersHQ.SendProxy.Example/YappersHQ.SendProxy.Example.dll` | example consumer |
| `.build/gamedata/yappershq.sendproxy.jsonc` | gamedata signatures |

## 🙏 Credits

Port of SourceMod's **SendProxy** to ModSharp / CS2. The signature-finding helpers in `tools/` build on nosoop's `makesig`. The CS2 flattened-serializer encode path was reverse-engineered for this port — see [docs/](docs/).

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
