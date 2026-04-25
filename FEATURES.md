# Postmortem — Feature Inventory

Standalone CounterStrikeSharp plugin. Admin tool for dealing with deaths after the fact: browse recent deaths, respawn one or many at the captured death position, and play back movement (victim **and** killer) as ghost props.

No external dependencies beyond CSSharp itself.

---

## 1. Death capture

`EventPlayerDeath` → `DeathStack.Push(DeathEntry)`. Each death is a fresh entry (no slot dedup — die, get respawned, die again all tracked independently).

**Stable IDs.** `DeathStack.Push` stamps a monotonic `Id` on every entry. IDs reset on `EventRoundStart` / `OnMapStart`, but never shift mid-round — popping `#5` leaves `#3` and `#7` unchanged. User commands type the id directly (`!replay 7`, `!sres #7`).

**Stack scope.** Round-bounded. `EventPlayerDisconnect` removes *all* entries for that slot.

**Safety cap** (`pm_max_deaths_stored`, default 100): FIFO eviction when exceeded; eviction counter exposed via `!pmstats`.

**Captured fields.** `MovementHistory` (victim's recent ring buffer), `KillerMovementHistory` (killer's recent ring buffer, non-destructive peek), `Events` (sparse `ReplayEvent` log), `KillerSnapshot` (position/rotation/weapon/hitgroup at kill time), `DeathPosition` + `DeathAngles` (always captured, regardless of `pm_replay_enabled`), `VictimTier` (NotArmed / Pistol / Primary — drives ghost tint).

---

## 2. Respawn — `!sres`, `!sresevent`

`!sres [count|#id|name] [spawn|death]` — one command, four shapes:
- no arg → newest 1 (T-only)
- numeric `N` → last N **T** deaths from the stack top (count form skips CT entries via `PopLastNTerrorist`)
- `#N` → specific stable id (any team)
- bareword → newest victim-name substring match (any team)

`!sresevent [event_id] [spawn|death]` — revive **T members** of a chain. Default = newest **T** event (`NewestTerroristDeathId` skips a trailing CT self-slay). `event_id` is any death-id within the chain; `DeathStack.PopGroupTerroristContainingId` walks the chain bounds via the `pm_group_gap_seconds` rule and pops only T-team entries — CT entries in the same chain remain in the stack.

**T-only filter on bulk flows.** `!sres N` and `!sresevent` are freekill-response tools: a CT slaying themselves as punishment shouldn't burn one of the N slots, and bulk-reviving a CT during a T rebellion isn't usually intended. Internal CT incidents are rare (fewer CTs) and handled case-by-case via `!sres #id`. Browse + replay paths (`!deaths`, `!devents`, `!replay`, `!replayevent`) ignore the team filter so CT entries stay fully inspectable.

**Default location: death position**, with team-spawn fallback when no position was captured (rare disconnect-race case). Override with trailing `spawn` / `team`. Respawn defers a frame so the engine has time to revive the pawn before teleport.

**Skips silently:** disconnected, already alive, on team Spectator/None. Reports `Respawned X/Y: …` or `Respawned 0/Y — all alive or disconnected`.

**Consume-wins:** if the popped entries belong to the active replay, the replay cancels.

---

## 3. Replay — `!replay`, `!replayevent`, `!stopreplay`

`!replay [id|name]` plays one death (default newest). `!replayevent [event_id]` plays every member of an event together. Both default to newest target when called without an arg.

**Channels.** Each victim is one `MemberChannel`. When `KillerMovementHistory` is present and the killer isn't already a victim in the same group, an additional **killer-companion channel** is added so the CT animates alongside the T.

**End-aligned timeline.** Channels share a single `_frameIndex`. `TotalFrames = max(channel.Frames.Length)`; each channel has `StartFrameDelay = TotalFrames - Frames.Length`, so all channels finish on the same wall-clock kill tick. Shorter channels spawn lazily once `_frameIndex` reaches their delay.

**Visuals.**
- Victim ghost: green glow companion, model tinted by `VictimTier` (NotArmed = natural, Pistol = yellow, Primary = red).
- Killer ghost: red glow companion, natural model color.
- Look beam (lime, 1.0 thick) follows eye direction.
- Shot beams (red) flash on frames with `ShotDirection`.
- Kill-shot beam (blue, killer head → victim head) drawn on the victim's final tick. Skipped for suicides (`KillerSlot == VictimSlot`) and world kills (`KillerAt is null`).

**Multi-victim de-dup.** A single killer who killed N victims in one event renders as one channel, not N. Killers who are also victims within the event are skipped as companions (they'd render twice).

**Concurrency.** One active replay at a time. Cancellation paths: `!stopreplay`, new replay, `EventRoundStart`, `OnMapStart`, `Unload`, and `!sres` / `!sresevent` consuming a member.

**Denial.** If every member's `MovementHistory` is null/empty (recording was off at death time), the command refuses with `No replay data for …`.

---

## 4. Browse — `!deaths`, `!devents`

`!deaths` — newest-first individual list. Rows show `#id (ago) victim — killed by killer` (or `suicide`). Victim names tinted yellow, killer names tinted red.

`!devents` — newest-first chain list. Rows show `#id (ago) N deaths in Xs: name1, name2, …`. Event id = the chain's newest member id; any id within the chain resolves the same chain via `FindGroupContainingId`.

Both list headers carry an inline command hint so admins don't have to memorize the verbs.

---

## 5. Player-facing — `!fk`

`!fk` (no admin flag): the caller's most recent death is flagged as a freekill complaint. Staff with `@css/generic` receive a chat alert containing the `!replay <id>` command — the command portion is colored `LightYellow` against the red urgency line so it stands out.

Triggered by either the `!fk` command or by typing `fk` / `freekill` as a standalone word in chat. Dead-only — living callers are silently filtered (chat) or get a one-line nudge (command). Per-caller cooldown via `pm_fk_cooldown_seconds`.

---

## 6. Staff chain alert

When a chain closes (no new death within `pm_group_gap_seconds`) and the **T**-victim count meets `pm_event_alert_min_deaths` (default 3), staff receive `⚠ N-kill chain just ended — !replayevent <id>` where `N` is the T count and the `id` anchors on a T death.

**T-only counting.** Only T pushes increment the counter and update `_chainAnchorId`; CT pushes still extend the debounce timer (so a mid-chain CT slay doesn't close the chain early) but don't count toward the threshold or anchor the alert. Pure CT chains and T-rebellion CT-only chains never alert. The alert id is always a T death so `!replayevent <id>` lands on a freekill victim.

**Debounce.** Each death push (T or CT, once an anchor exists) extends the timer to `gap + 0.5s`. The chain stays "open" as long as kills keep landing inside the gap. The alert fires exactly once when the chain closes.

**Consume-suppression.** At fire time the stack is re-queried via `FindGroupContainingId`; T members in the still-present group are re-counted. If an admin already consumed the event with `!sresevent` (T count dropped below threshold), no alert fires.

Set `pm_event_alert_min_deaths 0` to disable. Reset on round/map/unload.

---

## 7. Movement sampler

Timer-driven. Adaptive interval keyed to alive-combatant count: `≤10 → ticks_min`, `≤20 → +1`, `≤30 → +2`, `≤40 → +3`, `>40 → ticks_max`. Rate is recomputed at `EventRoundStart` and on live ConVar change (see §9).

**Per-slot ring buffer.** Fixed-capacity, head-and-count, sized to `ceil(window_seconds × Hz) + 8` and clamped to `[16, 2048]`. `MovementFrame` slots are reused in place — zero per-sample heap allocation. String fields are interned.

**Snapshots.**
- `SnapshotForDeath(slot)` — copies frames into a fresh array and clears the live ring (victim is dead — fresh window for next life).
- `PeekFramesForSlot(slot)` — non-destructive copy; used at kill time to grab the killer's recent buffer without disturbing their ongoing window.

**`PlayerCache`** (from `menn-utils`) replaces `Utilities.GetPlayers()` at tick rate. Heal-on-miss counters in `!pmstats`.

---

## 8. Memory footprint

Per `MovementFrame`: ~72 B (per-frame estimate used by `!pmstats`); real heap closer to 150–200 B due to per-frame `Vector`/`QAngle` wrapper objects.

Per buffer (default `window=10s`, `ticks_min=6` at 64-tick): ~115 frames ≈ 8 KB.

| Setting | Live (65 slots) | Per death (V+K) | Stack at cap | Total |
|---|---|---|---|---|
| Defaults | ~540 KB | ~16 KB | ~1.6 MB | **~2 MB** |
| Aggressive (`window=20`, `ticks_min=2`, cap 500) | ~3 MB | ~94 KB | ~47 MB | **~50 MB** |
| Max settings (`window=60`, `ticks_min=1`, cap 2000) | ~9.5 MB | ~295 KB | ~590 MB | **~600 MB** |

Round-bounded clearing keeps real-world stacks well below the cap.

---

## 9. ConVars (all live-tunable)

| ConVar | Default | Range | Notes |
|---|---|---|---|
| `pm_replay_enabled` | `true` | bool | Master switch for sampling + replay. Live (Func read each tick). |
| `pm_replay_window_seconds` | `10.0` | 1–60 | Rolling window per player. Live via `ValueChanged` → `MovementSampler.RebuildBuffers`. |
| `pm_replay_sample_ticks_min` | `6` | 1–32 | Sample interval at ≤10 alive. Live via `ValueChanged` → `RecomputeInterval`. |
| `pm_replay_sample_ticks_max` | `10` | 1–32 | Sample interval at >40 alive. Live via `ValueChanged`. |
| `pm_replay_linger_seconds` | `5.0` | 0–30 | Replay-end linger. Live (Func per Tick in `MovementReplay`). |
| `pm_max_deaths_stored` | `100` | 32–2000 | Stack cap. Live (read on each push). |
| `pm_group_gap_seconds` | `1.5` | 0–60 | Chain-link gap. Live (read per command + chain alert). |
| `pm_event_alert_min_deaths` | `3` | 0–64 | Staff alert threshold (0 = off). Live. |
| `pm_fk_cooldown_seconds` | `30.0` | 0–600 | `!fk` cooldown. Live. |
| `pm_chat_prefix` | `pm` | string | Chat tag. Live. |

---

## 10. Data structures

| Type | File | Notes |
|---|---|---|
| `DeathEntry` | `DeathStack.cs` | `Id` is init-only, stamped by `Push`. `MovementHistory` / `KillerMovementHistory` / `Events` / `KillerAt` are null when sampling was off (or no killer). `DeathPosition` / `DeathAngles` always captured. `VictimTeam` is captured at death time (drives the T-only filter on `!sres N`, `!sresevent`, chain alert). |
| `MovementFrame` | `Replay/MovementFrame.cs` | Reference type, reused in ring slots. `ShotDirection` non-null only on frames where the player fired. |
| `KillerSnapshot` | `Replay/ReplayEvent.cs` | `KillerSlot == VictimSlot` ⇒ suicide (bot SteamIDs all collide at 0). |
| `ReplayEvent` (`ShotFired` / `DamageDealt` / `DamageTaken` / `WeaponPickup`) | `Replay/ReplayEvent.cs` | `At` is seconds since round start; same timeline as `MovementFrame.TimeSinceRoundStart`. |

---

## 11. Perf

Measured on cs2-jb-lab @ 64 tick × 16 bots, recording=on, ~10.7 Hz sampling:

| Op | p50 | p95 | p99 |
|---|---|---|---|
| `replay.sample_one_player` | ~3 µs | ~33 µs | ~45 µs |
| `replay.sampler_tick` | ~90 µs | ~110 µs | ~130 µs |
| `replay.death_snapshot` | ~27 µs | ~730 µs | ~730 µs |

Tick budget at 32 players × 10.7 Hz ≈ 0.6% of a 15625 µs server frame. p99 spikes (~few ms `max`) are rare Gen1 GC / JIT stalls; never sustained.

`css_pmperfbench` runs a synthetic ns/op benchmark for any server.

---

## 12. Known gotchas

- **`prop_dynamic at (0,0,0) has no model name!`** — harmless engine warning during ghost spawn (`DispatchSpawn` is called before `SetModel`). Reversing the order breaks the prop entirely.
- **Suicide / world-kill detection** uses `KillerSlot == VictimSlot` (bot SteamIDs collide at 0) plus `KillerAt is null` for fall/world damage. Both paths skip the kill-shot beam and the killer ghost.
- **Animgraph-driven models.** Modern CS2 player models are graph-driven — `SetAnimation` calls log warnings and don't take effect. Replay relies on `UseAnimGraph=true` and the model's default idle pose; position/orientation/velocity all track correctly via `Teleport`.
