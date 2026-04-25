# menn-utils

Public helper library for [CounterStrikeSharp](https://docs.cssharp.dev/) plugins. Generic, branding-free utilities — split out of an internal helper library so externally-distributed plugins can consume them without dragging community-specific code along.

`menn-` prefix is for namespace safety only (collision avoidance vs someone else's `cssharp-utils`). Nothing community-specific in this assembly. If you decompile a plugin that ships with `menn-utils.dll`, what you'll find is what's documented below.

## What's in here

| Type | Use |
|---|---|
| `Menn.Utils.PlayerCache` | Slot-indexed player cache. CSSharp's `Utilities.GetPlayers()` is O(N) with native lookups per slot — too expensive for hot paths at 10+ Hz. This cache wires itself to the standard CSSharp lifecycle events (connect, disconnect, team, spawn, death) and exposes O(1) slot lookup, heal-on-miss counters, optional `OnAdd`/`OnRemove`/`OnTeamChange` callbacks for adapter layers, and `SyntheticBotId(slot)` helpers for plugins that need to key on SteamID64 without bot collisions. |
| `Menn.Utils.PerfTracker` | Per-op latency percentiles for hot-path diagnostics. ~100–200 ns overhead per `Measure(opName, action)` call, bounded 1024-sample ring per op, percentile snapshots on demand. Use to catch handlers that are stealing frames before players feel them. |
| `Menn.Utils.TargetResolver` | Admin-command target parser. Resolves a string to a player via SteamID64 → `#slot` → case-insensitive name substring. Returns `Resolved` / `Ambiguous` / `NotFound` with a candidate list so command handlers can render useful error messages. |

## Using it from a plugin

```xml
<!-- YourPlugin.csproj -->
<PropertyGroup>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\menn-utils\menn-utils.csproj" />
</ItemGroup>
```

`menn-utils.dll` lands beside your plugin assembly. Zero external dependencies beyond what CSSharp already provides (`CounterStrikeSharp.API` + `Microsoft.Extensions.Logging.Abstractions`).

## PlayerCache lifecycle (read once, stop bleeding)

Wire the cache up once in `Load`:

```csharp
public override void Load(bool hotReload) {
    _cache = new PlayerCache(Logger);
    _cache.Start(this, hotReload);
}
```

`Start` handles every load shape — these are the ones that bit us, encoded in the cache so consumers don't have to think about them:

- **Cold load on a fresh server boot.** Calling `Utilities.GetPlayers()` synchronously inside `Load` can fault with `"callback on GCed delegate"` — the engine's slot system isn't always wired yet. `Start(plugin, hotReload: false)` defers the reconcile with `Server.NextFrame(Reconcile)`. Don't call `Reconcile()` yourself from `Load`.
- **Hot reload mid-session.** Already-connected players won't re-fire `OnClientPutInServer` or `EventPlayerConnectFull` for the freshly loaded plugin instance — the cache would stay empty until the next map. `Start(plugin, hotReload: true)` reconciles synchronously (engine is hot, no GC fault).
- **`Reconcile()` can briefly return an empty `Utilities.GetPlayers()`** during the same engine-timing race. The cache mitigates this with **heal-from-event**: `EventPlayerSpawn` / `EventPlayerTeam` / `EventPlayerDeath` `AddOrUpdate` the slot when it's missing, so any per-slot event the plugin observes seeds the cache. Without this you can hot-reload mid-game, observe deaths just fine, and end up with a permanently empty cache because the connect events never re-fired and the reconcile happened to land in the empty window.
- **`bot_add` mid-session can skip `EventPlayerConnectFull`** on some CS2 builds. The cache subscribes to `OnClientPutInServer` alongside as belt-and-braces; `AddOrUpdate` is idempotent.
- **Map changes** call `Reconcile()` automatically via `Listeners.OnMapStart`.

### Hot-path consumers — use `GetOrHeal`, watch `MissCount`/`HealCount`

If a sampler-style consumer asks about a slot that hasn't been cached yet (race between connect event and first tick), `GetOrHeal(slot)` re-fetches via `Utilities.GetPlayerFromSlot`, caches it, and increments the heal counter. Non-zero `MissCount` / `HealCount` in steady state is your signal that a CSSharp lifecycle event is firing on the wrong slot or that something is racing — surface them in your stats command (the postmortem `!pmstats` output is the reference shape).

### Bots: `SteamID = 0` collides

Every bot reports `SteamID = 0`. Don't key data structures on raw `SteamId` if bots can be present. Use `PlayerCache.SyntheticBotId(slot)` to mint a non-colliding 64-bit id (`0xF000_…` prefix is outside the real SteamID64 range) and `IsSyntheticBotId(id)` to test on the way back.

## Versioning

No semver yet — bundled per consumer. If/when this gets published as a NuGet package, breaking changes will follow standard rules.
