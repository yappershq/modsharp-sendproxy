# How SendProxy works

SendProxy lets a server plugin change **what a client receives** for a networked entity field —
fake HP, a different team, a spoofed name, a cycling colour — **without touching the real
server-side value**. The pawn still has 100 HP on the server; the player just *sees* 1.

This article explains what that takes on Counter-Strike 2 (Source 2), how the library is built, and
what it can and can't do. It assumes you know roughly what a "networked variable" (netvar) is, but
not how CS2 serializes them.

---

## 1. The problem: Source 2 has no per-field proxy

On Source 1 (CS:GO and earlier), faking a value per client was easy. Every networked field had a
`SendProp` with a `m_pProxyFn` — a function the engine called *while serializing each client's
snapshot*. A plugin swapped that function pointer and returned whatever it wanted, per client, for
free.

Source 2 threw that away. CS2's networking works in two stages:

1. **Pack once.** Each tick, the server encodes **every** changed entity field **one time** into a
   shared `CFrameSnapshot` — a big buffer of already-bit-packed values. This is the expensive part,
   and it's done once for the whole server (often across worker threads).
2. **Delta per client.** For each client, the server writes a delta that **copies the pre-encoded
   bits** out of that shared snapshot. It does *not* re-encode anything per client.

So by the time the per-client step runs, the value is already frozen into bits that every client
shares. Native per-client differentiation exists only for **presence** (a recipient bitmask decides
*who* receives a field), never for **value**.

That's the whole challenge: to fake a value, you either change the bits *before* the shared pack
(everyone sees it), or you rewrite the bits *during* the per-client copy (one client sees it).
SendProxy does both.

---

## 2. Two substitution paths

SendProxy has two independent mechanisms, chosen automatically by which output method the callback
calls.

### Uniform — intercept the encoder (everyone sees the same fake)

CS2 encodes each field through a small set of **encoder functions** (one per value family — see
§3). SendProxy intercepts those functions via an `IMidFuncHook` (observe-and-resume) at the
per-entity encode wrapper (`CFlattenedSerializer::EncodeEntity`). When a field you've registered is
being packed, the hook points the encoder at a **scratch value** holding your fake instead of the
real one, then lets the original encoder run. The fake bits go into the shared snapshot, so
**every** client that receives that field sees the fake.

This is cheap (the encoder runs once, as usual) and is what `SetAll(value)` in the callback
produces.

> **Why an `IMidFuncHook`, not a detour?**
> Detouring the recursive `EncodeEntity` path causes a stack overflow — each recursive call re-enters
> the detour. `IMidFuncHook` (observe-and-resume, a mid-function hook) lets the library intercept the
> field write point without re-entering on recursion. See
> [`docs/ENCODE_CALLGRAPH_RE.md`](ENCODE_CALLGRAPH_RE.md) for the full crash and hook-type analysis.

### Per-client — rewrite the bits in the send loop (each client can differ)

For different values per recipient, the encoder hook is useless (it runs once, with no idea who the
packet is for). Instead SendProxy hooks the **per-client delta writer**:

- A hook on the per-client encode entry captures **which client** is currently being written
  (stored in a thread-local for that client's encode chain).
- A hook on `WriteFieldList` (the function that copies each changed field's bits into the
  client's delta) intercepts the per-field copy. For a field with a per-client override it uses the
  value supplied via `SetFor`, encodes it into a small scratch buffer, and copies **those** bits
  into the client's stream instead of the real ones.

Because this runs per client, each recipient can get a different value — true per-client spoofing.
`SetFor(client, value)` in the callback uses this path. (Deep detail:
[`RE_PERCLIENT_EMIT.md`](RE_PERCLIENT_EMIT.md).)

> **Key insight that made per-client work:** the encoder can't be driven straight into the live
> per-client value buffer at its mid-stream cursor reliably. Instead SendProxy encodes the fake into
> a fresh, byte-aligned scratch buffer and then calls the engine's **own** bit-copy primitive to
> splice those bits in — the exact same copy the engine uses for every real field. The only
> non-engine step is producing the fake bits; the write into the live stream is 100 % engine code.

---

## 3. Encoder buckets (the value families)

CS2's encoder registry is a table of 8 "buckets", each holding the encoders for one value family.
SendProxy resolves this table from gamedata at load and classifies every field by which encoder it
uses. The buckets:

| Bucket | Family | Example fields |
|---|---|---|
| b1 | signed int (zigzag varint) + fixed32/fixed64 | `m_iHealth`, `m_ArmorValue` |
| b2 | unsigned int (varint) + fixed32/fixed64 | `m_iTeamNum`, `m_iPawnHealth` |
| b3 | float family: `default`(quantized), `qangle`, `qangle_pitch_yaw`, `qangle_precise`, `normal`, `coord`, `coord_integral` | `m_angEyeAngles`, position, `m_flViewmodelFOV` |
| b4 | float32 (raw) | various `[32 bits noscale]` floats |
| b5 | string (null-terminated byte stream) | `m_iszPlayerName` |
| b6 | byte array (`{data, count}`) | rare on players |
| b7 | bool (1 bit) | `m_bIsScoped` |

Each encoder reads its value pointer differently (a varint reads an integer; the string encoder
reads a `char**`; the quantized encoder reads a struct with a component count). SendProxy builds the
fake in exactly the layout each encoder expects. The full per-encoder table is in
[`RE_ENCODERS.md`](RE_ENCODERS.md).

A field whose encoder SendProxy doesn't recognise is **passed through untouched** — it never
corrupts the stream.

---

## 4. The API

Everything goes through `IProxyManager` (resolve it in `OnAllModulesLoaded`, not `Init`/`PostInit`
— ModSharp doesn't order publish across plugins; cache the handle and read `.Instance` per use so a
SendProxy hot-reload can't dangle you).

Fields are addressed by `(serializerName, fieldName)`, e.g. `("CCSPlayerPawn", "m_iHealth")`.

The callback delegate is `ProxyCallback(ref ProxyContext ctx)`. It fires **once per (entity, field)**
during the shared `PackEntities` pass — never per recipient. O(1) in player count.

### Uniform — same value for everyone

```csharp
// Every client sees 1 HP for every player pawn.
_proxy.Register("CCSPlayerPawn", "m_iHealth", static (ref ProxyContext ctx) =>
{
    ctx.SetAll(SpoofValue.Int(1));
});

_proxy.Register("CCSPlayerController", "m_iszPlayerName", static (ref ProxyContext ctx) =>
{
    ctx.SetAll(SpoofValue.String("Hacker"));
});

_proxy.Register("CCSPlayerPawn", "m_bIsScoped", static (ref ProxyContext ctx) =>
{
    ctx.SetAll(SpoofValue.Bool(true));
});
```

### Per-client — a value delivered only to a specific recipient

```csharp
// Only `viewer` sees this pawn's health as 1. Runs on pack worker — keep it fast and pure.
_proxy.Register(pawn, "CCSPlayerPawn", "m_iHealth", (ref ProxyContext ctx) =>
{
    ctx.SetFor(viewer, SpoofValue.Int(1));
});
```

`SetFor` may be called multiple times in one invocation to give different clients different values.
Clients that receive no `SetFor` call see the real value.

### Per-entity — scope to one entity

```csharp
_proxy.Register(entity, "CCSPlayerPawn", "m_iHealth", static (ref ProxyContext ctx) =>
{
    ctx.SetAll(SpoofValue.Int(1));      // only this entity
});

_proxy.Register(entity, "CCSPlayerPawn", "m_iHealth", (ref ProxyContext ctx) =>
{
    ctx.SetFor(viewer, SpoofValue.Int(1));  // per-client + per-entity
});
```

### Removing

```csharp
_proxy.Unregister("CCSPlayerPawn", "m_iHealth");           // all-entities
_proxy.Unregister(entity, "CCSPlayerPawn", "m_iHealth");   // entity-scoped
```

---

## 5. Two things that trip everyone up

### "It only shows after the value changes"

Registering a proxy doesn't re-send anything. A static field (HP that isn't changing, a name) isn't
re-transmitted until it **changes**, so the fake only appears on the next natural change. To show it
immediately, force the field dirty — `entity.NetworkStateChanged("m_iHealth")` — which schedules a
re-send. (That's also why, after `Unregister`, the client keeps the last fake until you re-dirty the
field so the real value goes back out.) See `docs/FORCE_RESEND.md` for more detail.

### "Some values live in two places"

HP is mirrored: the pawn's `CCSPlayerPawn::m_iHealth` is the real value (and drives your own HUD),
while `CCSPlayerController::m_iPawnHealth` is the controller mirror used by the scoreboard, observer
UI, and what other players see. To fake HP everywhere you proxy **both**. Most fields (team, name,
scoped, …) live in a single place.

Also note **whose view a field affects**. `m_angEyeAngles` changes where a player *appears to be
looking* — you can't see your own eye angles, only others' (or via spectating). A "fake HP" you set
on yourself shows on your own HUD; a fake you set on a bot shows when you look at the bot.

---

## 6. Safety & lifecycle

- **Native pointer safety.** Every native read is guarded by a user-space pointer check, not by
  `try/catch` — a bad dereference is a fatal access violation that .NET cannot catch, so the pointer
  guard is the only real protection.
- **Unknown encoders pass through.** If a field's type can't be safely synthesised, the real value
  is written unchanged.
- **Consumer unloads.** When a consumer plugin unloads, SendProxy purges that module's callbacks, so
  the send path never invokes a delegate into an unloaded assembly.
- **Hook install.** The `IMidFuncHook` on the per-entity encode wrapper and the per-client delta
  hooks are installed once at load (not lazily mid-game), avoiding a frame stall the first time a
  proxy is registered.

---

## 7. What it can't (cleanly) do

- **Nested sub-vectors by name.** Fields split into sub-components (`m_vecViewOffset.m_vecX/Y/Z`,
  origin) share bare leaf names (`m_vecX`) across different vectors, and uniform matching is by field
  name — so they can't be uniformly targeted without colliding. Position-style spoofs need the
  per-client path's full-path resolution.
- **Fields the client doesn't render from the netvar.** Some values are client-predicted or
  cvar-driven (e.g. viewmodel FOV); spoofing the netvar then has no visible effect even though the
  bits are written correctly.

---

## 8. Going deeper

- [`RE_ENCODERS.md`](RE_ENCODERS.md) — every encoder's value-pointer convention.
- [`RE_PERCLIENT_EMIT.md`](RE_PERCLIENT_EMIT.md) — the per-client write path, decompile-cited.
- [`ENCODE_CALLGRAPH_RE.md`](ENCODE_CALLGRAPH_RE.md) — crash analysis and hook-type choice for the
  per-entity encode wrapper (`CFlattenedSerializer::EncodeEntity`).
- [`REVERSE_ENGINEERING.md`](REVERSE_ENGINEERING.md) — the full pipeline RE (snapshot pack, send
  loop, gamedata signatures).
