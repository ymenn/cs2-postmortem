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

## Versioning

No semver yet — bundled per consumer. If/when this gets published as a NuGet package, breaking changes will follow standard rules.
