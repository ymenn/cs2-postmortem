# Postmortem

Admin forensic tool for [CounterStrikeSharp](https://docs.cssharp.dev/) CS2 servers. Respawn the last N dead players, browse recent deaths, and play back captured movement as ghost props.

No external dependencies beyond CSSharp itself. Vendored [`menn-utils`](./menn-utils/README.md) under `menn-utils/`.

## Commands (all `@css/generic` unless noted)

| Command | Effect |
|---|---|
| `!pmres [N]` | Respawn last N individual deaths (no grouping). Default `N=1`. |
| `!pmdeaths` / `!pmd` | List recent individual deaths with `#id`. |
| `!pmreplay [id\|name]` / `!pmr` | Play back individual death `#id`, newest name-match, or newest (default). |
| `!pmreplaystop` / `!pmrs` | Cancel active replay. |
| `!pmevents` / `!pmev` | List recent death-events (for mass-respawn flow). |
| `!pmresevent <id>` / `!pmre` | Respawn everyone in event `#id`. |
| `!pmstats` | Storage footprint + sampler counters. |
| `!pmrecording [on\|off\|toggle]` / `!pmrec` | Toggle `pm_replay_enabled` live. |

Debug (`@css/root`): `css_pmstack`, `css_pm_killbot`, `css_pmperfbench`, `css_pmreplay_status`.

Full feature/design notes: [`FEATURES.md`](./FEATURES.md).

## ConVars

| ConVar | Default | Purpose |
|---|---|---|
| `pm_replay_enabled` | `true` | Master switch for movement sampling + replay. |
| `pm_replay_window_seconds` | `10.0` | Rolling window per player. |
| `pm_replay_sample_ticks_min` | `6` | Sample interval (ticks) when ≤10 players alive. |
| `pm_replay_sample_ticks_max` | `10` | Sample interval when >40 alive. |
| `pm_replay_linger_seconds` | `5.0` | How long replay entities stay on screen after playback. |
| `pm_max_deaths_stored` | `100` | Safety cap on death stack. FIFO eviction above. |
| `pm_group_gap_seconds` | `1.5` | Chain-link gap for event grouping. |
| `pm_chat_prefix` | `pm` | Chat-line tag (`"pm"` → `[pm]`). |

## Building

```sh
dotnet build -c Release
```

Output lands in `bin/Release/net8.0/`. For iterative development against a live server, create `postmortem.local.props` (gitignored) pointing at your CSSharp install:

```xml
<Project>
  <PropertyGroup>
    <CsSharpInstall>/path/to/addons/counterstrikesharp</CsSharpInstall>
  </PropertyGroup>
</Project>
```

Build then drops the bundle straight into `$(CsSharpInstall)/plugins/Postmortem/`.

## Installing

Drop the Release build output into your server's `addons/counterstrikesharp/plugins/Postmortem/` folder. The bundle is:

```
plugins/Postmortem/
├── Postmortem.dll
├── Postmortem.deps.json
├── menn-utils.dll
└── lang/
    ├── en.json
    └── pt.json
```

Reload in-server without restarting:

```
css_plugins reload Postmortem
```

## Localisation

`lang/en.json` and `lang/pt.json` (European Portuguese) ship. CSSharp picks a language per the connecting player's locale. To add a language, copy `lang/en.json` to `lang/<code>.json` and translate the values — keys must match.

## License

Unspecified.
