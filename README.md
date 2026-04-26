# Postmortem

Admin forensic tool for [CounterStrikeSharp](https://docs.cssharp.dev/) CS2 servers. Browse recent deaths, respawn the last N (or specific) victims, and play back captured movement as ghost props — including the killer alongside the victim.

No external dependencies beyond CSSharp itself. Vendored [`menn-utils`](./menn-utils/README.md) under `menn-utils/`.

## Commands

All `@css/generic` unless noted. Legacy `!pm*` aliases retained for every command.

### Browse

| Command | Aliases | Effect |
|---|---|---|
| `!deaths` | `!pmdeaths`, `!pmd` | List recent individual deaths with stable `#id`s. |
| `!devents` | `!pmevents`, `!pmev` | List recent death-events (chains grouped by `pm_group_gap_seconds`). |

Death `#id`s are stable within a round — they're assigned monotonically at kill time, never shift, and are reset only on round/map start. Event `#id`s use the newest member's death-id and resolve via any member of the chain (any id you saw still works after newer kills join the event).

### Respawn

| Command | Aliases | Effect |
|---|---|---|
| `!sres [count\|#id\|name] [@spawn\|@death\|@here\|@aim]` | `!pmres` | Revive last N T deaths (default 1), specific death-id (any team), or newest name match. |
| `!sresevent [event_id] [@spawn\|@death\|@here\|@aim]` | `!sresev`, `!pmresevent`, `!pmre` | Revive T members of an event (default: newest T event). |

Default respawn location is the captured **death position** (with team-spawn fallback when none was captured). Trailing keyword overrides:

- `@spawn` — engine team spawn.
- `@death` — captured death position (the default).
- `@here` — 80u in front of the caller's facing direction (like Admin `!bring`). Requires a live caller in-game. When reviving multiple players, they all land at the same spot.
- `@aim` — reserved for eye-trace destination; currently unavailable (same CSSharp signature break as Admin `@aim`).

**T-only filter on bulk flows.** `!sres N` and `!sresevent` skip CT entries because these commands target freekill response — a CT slaying themselves as punishment shouldn't burn one of the slots, and bulk-reviving a dead CT during a T rebellion isn't usually wanted. CT entries stay in the stack for `!sres #id`, `!replay`, and `!devents` — internal CT incidents (rare, fewer CTs) are handled case-by-case via explicit id.

### Replay

| Command | Aliases | Effect |
|---|---|---|
| `!replay [id\|name]` | `!pmreplay`, `!pmr`, `!var` | Play back one death (default: newest). Renders victim ghost (green glow) plus animated killer ghost (red glow) when killer movement was captured. |
| `!replayevent [event_id]` | `!pmreplayevent`, `!replayev`, `!pmrev`, `!varevent`, `!varev` | Play back every member of an event together (default: newest event). |
| `!stopreplay` | `!pmreplaystop`, `!stopr`, `!pmrs` | Cancel active replay. |

Replay chat lines (kill-shot, fired weapon, swung knife) are tagged with `[+X.Xs]` showing time since playback started, so admins can correlate each action with the timeline. When a replay finishes naturally, staff get a green chat line with the matching `!sres #<id>` or `!sresevent <id>` ready to copy.

### Other

| Command | Aliases | Effect |
|---|---|---|
| `!fk` | — | Player-callable. Flag your most recent death as a freekill; staff get a chat alert with the colored `!replay <id>` command. Also fires when "fk" / "freekill" appears as a standalone word. Dead-only, per-caller cooldown. |
| `!pmstats` | — | Storage footprint + sampler counters. |
| `!pmrecording [on\|off\|toggle]` | `!pmrec` | Toggle `pm_replay_enabled` live. |

Debug (`@css/root`): `css_pmstack`, `css_pm_killbot`, `css_pmperfbench`, `css_pmreplay_status`.

## Staff alerts

When a chain of `pm_event_alert_min_deaths` (default 3) or more **T** deaths closes (no new kill within `pm_group_gap_seconds`), staff receive a chat line with the `!replayevent <id>` command. CT deaths in the chain extend the debounce timer (so a mid-chain CT slay doesn't close the chain early) but don't count toward the threshold — pure T-rebellion chains and CT-only deaths don't generate alerts. Suppressed if the event was already consumed via `!sresevent` before the chain closed. Set the threshold to `0` to disable.

## ConVars

All live-tunable (no reload required).

| ConVar | Default | Range | Purpose |
|---|---|---|---|
| `pm_replay_enabled` | `true` | bool | Master switch for movement sampling + replay. |
| `pm_replay_window_seconds` | `10.0` | 1–60 | Rolling window per player. |
| `pm_replay_sample_ticks_min` | `6` | 1–32 | Sample interval (ticks) when ≤10 players alive. |
| `pm_replay_sample_ticks_max` | `10` | 1–32 | Sample interval when >40 alive. |
| `pm_replay_linger_seconds` | `5.0` | 0–30 | How long replay entities stay after playback ends. |
| `pm_max_deaths_stored` | `100` | 32–2000 | Safety cap on death stack. FIFO eviction above. |
| `pm_group_gap_seconds` | `1.5` | 0–60 | Chain-link gap for event grouping. |
| `pm_event_alert_min_deaths` | `3` | 0–64 | Min chain size to alert staff (0 = disabled). |
| `pm_fk_cooldown_seconds` | `30.0` | 0–600 | Min seconds between `!fk` complaints per player. |
| `pm_chat_prefix` | `pm` | string | Chat-line tag (`"pm"` → `[pm]`). |

## Building

```sh
dotnet build -c Release
```

Output lands in `bin/Release/net8.0/`. For iterative development against a live server, create `postmortem.local.props` (gitignored):

```xml
<Project>
  <PropertyGroup>
    <CsSharpInstall>/path/to/addons/counterstrikesharp</CsSharpInstall>
  </PropertyGroup>
</Project>
```

Build then drops the bundle straight into `$(CsSharpInstall)/plugins/Postmortem/`.

## Installing

Drop the Release build output into your server's `addons/counterstrikesharp/plugins/Postmortem/`. The bundle is:

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

Full design notes: [`FEATURES.md`](./FEATURES.md).

## License

Unspecified.
