# Postmortem — Feature Inventory

> **Status: replay shipped (2026-04-24).** Movement sampler, death capture, event grouping, and ghost-replay playback all wired up and live-tested. Remaining: location selector (§1.2) and extension mechanism (§3).

Standalone CounterStrikeSharp plugin. Admin tool for dealing with deaths after the fact: respawn the last N dead without needing their names, browse recent death-events, and play back captured movement as ghost props.

No external dependencies beyond CSSharp itself.

---

## 1. Core gameplay

### 1.1 Respawn last N individual deaths — `!pmres [N]`

`!pmres [N]` → respawn the N most-recently-dead players. **Per-death (no grouping).** `N` defaults to 1. Permission `@css/generic`.

Group-aware respawn lives in `!pmresevent` (§1.4). `pm_group_gap_seconds` no longer controls `!pmres` — it only governs event formation.

**Death tracking:** `EventPlayerDeath` → `DeathStack.Push(DeathEntry)`. Each death creates a new entry unconditionally (no slot dedup — a player can die → be respawned → die again in the same round, and both deaths are tracked independently).

**Stack scope:** round-bounded. `EventRoundStart` wipes. `EventPlayerDisconnect` removes *all* entries for that slot.

**Consumption:** `PopLastN` removes popped entries from the stack so the same death is never respawned twice. Already-alive or disconnected slots inside the popped set are skipped silently; the report line calls out the X/N respawn count.

**Safety cap (`pm_max_deaths_stored`, default 100):** FIFO eviction when exceeded — oldest entry is dropped and a warning is logged. Eviction counter exposed via `!pmstats`.

### 1.2 Respawn locations *(planned)*

Still planned. Current respawn always uses `c.Respawn()` (engine-picked team spawn). See `§1.2` in previous revisions for proposed options (`spawn` / `inplace` / `aim` / `marker`). Gated on the extension contract in §3.

### 1.3 Death replay *(shipped — `!pmreplay`)*

Movement sampler + ghost playback for JB-style forensics.

**Recording** (`pm_replay_enabled`, default `false`):
- Timer-driven, adaptive rate keyed to alive-combatant count (yappers-style table: `<=10 → 6 ticks`, `<=20 → 7`, `<=30 → 8`, `<=40 → 9`, `>40 → 10`). At 64 tick × 6 ticks = ~10.7 Hz.
- Per-slot ring buffer (fixed-size `MovementFrame[]` + head/count). Zero per-sample heap alloc — frame objects are reused in place; interned string references for `ModelName` + `ActiveWeaponDesignerName`.
- `Menn.Utils.PlayerCache` (shared library — see `menn-utils/README.md`) avoids `Utilities.GetPlayers()` at tick rate. Heal-on-miss counters exposed via `!pmstats`. Same cache backs gatekeeper.
- Event log sidecar (sparse `List<ReplayEvent>`): `ShotFired` / `DamageDealt` / `DamageTaken` / `WeaponPickup`. Tags `MovementFrame.ShotDirection` for shot-beam rendering.

**Death snapshot** (`EventPlayerDeath`):
- `Array.Copy` of the live ring buffer into a fresh `MovementFrame[]` attached to the `DeathEntry`. Live buffer resets for the slot's next life.
- Event log transferred (reference handoff).
- `KillerSnapshot` captures attacker's position, rotation, view angles, model name, weapon, hitgroup, damage + slot (for suicide detection).

**Playback** (`MovementReplay`):
- `CDynamicProp` + `SetModel(victim_model)` + four canned animations (`walk_new_rifle_stopped`, `crouch_new_rifle_stopped`, `idle_for_turns_stand_knife`, `idle_for_turns_crouch_knife`).
- **Green glow companion** — secondary `prop_dynamic` with `Glow.GlowColorOverride = Color.Green` attached via `FollowEntity` so the T pops visually without tinting the main model.
- **Look beam** (purple `CEnvBeam`) follows eye direction. Reused across frames.
- **Shot beams** (red) spawn on frames where `ShotDirection` is non-null.
- **Kill-shot beam** (blue) drawn from killer head → victim head on the final frame. Skipped for suicides (killer slot == victim slot).
- **Killer ghost** (prop_dynamic of attacker model at kill position) shown for non-suicide kills, purely positional — no animation timeline for the killer.

**Parallel multi-death:** one ghost per event member, all running on a unified timeline. Each ghost despawns at its own kill moment. By the end of the event window all ghosts are gone.

**Concurrency:** one active replay at a time. Starting a new one cancels the previous. Cancellation paths: `!pmreplaystop`, `EventRoundStart`, `OnMapStart`, `Unload`, and `!pmres` / `!pmresevent` consuming any entry (consume-wins).

**Denial path:** if every member's `MovementHistory` is null/empty (captured while recording was off), replay command prints `No replay data for event #id` and refuses. Recording state *at replay time* is irrelevant — only whether the data was captured.

### 1.4 Individual-death list + replay — `!pmdeaths` / `!pmreplay`

Individual deaths are the first-class object for browsing and replay.

- **`!pmdeaths`** — list recent individual deaths with `#1..#N` IDs (newest = `#1`). Shows victim, time-ago, and killer (or "suicide").
- **`!pmreplay [id|name]`** — play back one death. `id` = 1-based newest-first death id; `name` = newest death whose victim name contains the substring (case-insensitive). No arg = newest death. Does not consume.
- **`!pmreplaystop`** — cancel active replay.

### 1.5 Event grouping — `!pmevents` / `!pmresevent` (mass freekill respawn)

Secondary feature for mass-respawn flows. Deaths are chain-linked into events using `pm_group_gap_seconds` (refreshing gap; deaths at T=0, T=1.2, T=2.4 all form one group if gap ≥ 1.2). Events are not persistent objects — they're recomputed from the stack each time a command runs.

- **`!pmevents`** — list recent events with `#1..#M` IDs (newest = `#1`). Shows victim names, death count, duration, time-ago.
- **`!pmresevent <id>`** — respawn everyone in event `#id`. Consumes all member deaths from the stack.

All commands are `@css/generic`.

---

## 2. Commands

### Generic staff (`@css/generic`)

| Command | Effect |
|---|---|
| `!pmres [N]` | Respawn last N individual deaths (no grouping). Default `N=1`. |
| `!pmdeaths` | List recent individual deaths with `#id`. |
| `!pmreplay [id\|name]` | Play back individual death `#id`, newest name-match, or newest (default). |
| `!pmreplaystop` | Cancel active replay. |
| `!pmevents` | List recent death-events (mass-respawn flow). |
| `!pmresevent <id>` | Respawn everyone in event `#id`. |
| `!pmstats` | Storage footprint + sampler counters (deaths/cap, live buffers, memory, sampler tick count, pool stats). |
| `!pmrecording [on/off/toggle]` | Toggle `pm_replay_enabled` live. |

### Anyone (no admin flag required)

| Command | Effect |
|---|---|
| `!fk` | Flag the caller's most recent death as a freekill; staff (`@css/generic`) get a chat alert with the exact `!pmr #id` command. Also triggered when a player types `fk` / `freekill` as a standalone word in chat. Dead-only — living players are silently filtered (chat) or get a one-line nudge (command). Per-caller cooldown (`pm_fk_cooldown_seconds`, default 30s). |

### Debug (`@css/root`)

| Command | Effect |
|---|---|
| `css_pmstack` | Dump current death stack with ages + group boundaries + per-entry metadata. |
| `css_pm_killbot <name>` | Force-kill a bot by name — triggers a real `EventPlayerDeath` for testing. |
| `css_pmperfbench` | Run 10,000 synthetic sampler ops, report ns/op + p50/p95/p99 latency. |
| `css_pmreplay_status` | Dump active replay state + accumulated perf stats. |

---

## 3. Extension mechanism *(planned)*

Two-layer model: config toggles (ConVars + JSON) for shipped behavior, and a plugin-capability contract for custom respawn locations / replay renderers.

### 3.1 `IRespawnLocation` (stack contract, planned)

Would ship in a companion `Postmortem.Contracts.dll` so other plugins can add location providers without a hard reference to the main assembly.

```csharp
public interface IRespawnLocation {
    string Key { get; }    // stable identifier used in !pmres
    string Label { get; }  // human-readable for listings
    Task<RespawnPoint?> GetPointAsync(ulong targetSteamId, ulong invokerSteamId);
}

public record RespawnPoint(Vector Position, QAngle Angles);
```

- Async to permit cross-plugin / DB / trace lookups.
- Returns `null` when not resolvable (e.g., a warden-marker provider when no warden is set).
- Discovered from `extensions/*.dll` at load; keys must be unique (loader rejects duplicates with a clear log message).

### 3.2 Built-in implementations (when §1.2 lands)

- `SpawnLocation` — random team spawn.
- `InPlaceLocation` — death position + angles.
- `AimLocation` — invoker's aim-trace.

---

## 4. Config

### 4.1 Shipped ConVars

| ConVar | Default | Purpose |
|---|---|---|
| `pm_group_gap_seconds` | 1.5 | Chain-link gap for event formation. `0` = every death is its own event. Range 0–60. |
| `pm_replay_enabled` | true | Master kill-switch for movement sampling + replay. Off = no sampling. |
| `pm_replay_window_seconds` | 10.0 | Rolling window length per player. 10 s ≈ enough for rebellion context. Range 1–60. |
| `pm_replay_sample_ticks_min` | 6 | Sample interval in ticks when ≤10 players alive (64 tick / 6 ≈ 10.7 Hz). Range 1–32. |
| `pm_replay_sample_ticks_max` | 10 | Sample interval in ticks when >40 players alive. Range 1–32. |
| `pm_max_deaths_stored` | 100 | Safety cap on DeathStack depth. FIFO eviction + log warning when exceeded. Range 32–2000. |
| `pm_replay_linger_seconds` | 5.0 | Seconds replay entities stay on screen after the last frame plays. Range 0–30. |
| `pm_chat_prefix` | `pm` | Chat-line tag, wrapped in `[ ]` on output (`"pm"` → `[pm]`). |
| `pm_fk_cooldown_seconds` | 30.0 | Minimum seconds between `!fk` complaints per player (anti-spam). Range 0–600. |

### 4.2 Planned (JSON)

Full schema lands when §1.2 ships. Tentative keys: `DefaultRespawnLocation`.

---

## 5. Data structures

Source-of-truth definitions (read these for the current shape; this section
is just the map):

| Type | File | Notes |
|---|---|---|
| `DeathEntry` | `DeathStack.cs:8` | `MovementHistory`/`Events`/`KillerAt` are null when `pm_replay_enabled` was off at the time of death. |
| `MovementFrame` | `Replay/MovementFrame.cs:14` | Reference type, reused in `RingBuffer` slots. `ShotDirection` non-null only on frames where the player fired. `JbRoleFlags` reserved (zero in v1). |
| `KillerSnapshot` | `Replay/ReplayEvent.cs:21` | `KillerSlot == VictimSlot` is how suicide detection works (bot SteamIDs all collide at 0). |
| `ReplayEvent` (tagged union: `ShotFired`, `DamageDealt`, `DamageTaken`, `WeaponPickup`) | `Replay/ReplayEvent.cs:7-17` | `At` is seconds since round start, same timeline as `MovementFrame.TimeSinceRoundStart`. |

---

## 6. Perf

**Measured on cs2-jb-lab @ 64 tick × 16 bots, recording=on, 10.7 Hz:**

| Op | p50 | p95 | p99 | Notes |
|---|---|---|---|---|
| `replay.sample_one_player` | ~3 µs | ~33 µs | ~45 µs | Above 20 µs target; dominated by occasional schema-read stalls |
| `replay.sampler_tick` | ~90 µs | ~110 µs | ~130 µs | Well under 500 µs target |
| `replay.death_snapshot` | ~27 µs | ~730 µs | ~730 µs | Small n (3–10); warmup spikes |

Tick-budget impact at 32 players × 10.7 Hz ≈ 0.6% of a 15625 µs server frame — cheap. p99 spikes (~4 ms `max`) are rare Gen1 GC or JIT stalls; never sustained.

`css_pmperfbench` gives a single-number ns/op ground truth for any server; recommended when deciding whether to raise the sample rate.

---

## 7. Known gotchas

- **"prop_dynamic at (0,0,0) has no model name!"** engine warnings during replay startup — harmless, inherited from yappers' spawn order (`DispatchSpawn` before `SetModel`). Reversing the order breaks the prop entirely.
- **"no sequence named:walk_new_rifle_stopped"** on `tm_phoenix` model — canned anim names are character-model-specific; some models don't have the rifle-stopped idle. Replay still runs (position/orientation tracks), just without animation. Mitigated by only sending `SetAnimation` on anim-state change (stand↔crouch, knife↔rifle) instead of per-tick — so the engine logs one warning per transition instead of ~10/sec.
- **Suicide detection** uses `KillerSlot == VictimSlot`, not steam IDs (both bots have `SteamID=0`). Prevents the killer ghost / kill-shot beam from rendering on suicides while correctly showing them for bot-on-bot kills.

---

## 8. Open questions

1. Location returning `null` (when §1.2 lands) — skip that candidate, fall back to default, or refuse the whole command?
2. Scrub / pause / reverse during replay — the plan cut these for simplicity; user feedback may pull them back in.
3. ~~`PlayerCache` extraction~~ — done 2026-04-24. Lives in `menn-utils` (`Menn.Utils.PlayerCache`); postmortem references *only* `menn-utils` (no community-internal libraries) so the bundle stays externally shareable.
