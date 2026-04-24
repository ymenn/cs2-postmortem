# Postmortem — Live Testing Checklist

> Things verified bot-only over RCON are ticked. Things that need a real human
> client in-game or a sustained session to surface are listed unchecked — these
> are what the next live session should focus on.

Build + reload workflow (from postmortem/):
```sh
dotnet build
python3 /tmp/rc.py 'css_plugins reload Postmortem'
```

Baseline setup (de_dust2 with bots, no round timer):
```sh
python3 /tmp/rc.py 'mp_ignore_round_win_conditions 1; mp_warmup_end; bot_quota 12; mp_restartgame 1'
```

---

## Verified (bot-only, RCON)

- [x] `css_plugins reload Postmortem` hot-reloads cleanly — stack wipes, sampler
  re-wires, recording state persists off (default).
- [x] `!pmrecording on` flips cvar; `!pmstats` shows `replay=ON`. `off` / `toggle`
  both work.
- [x] Sampler ticks at adaptive rate: 16 alive → `interval=7 ticks` (~9.1 Hz);
  empty server → `interval=6 ticks` (~10.7 Hz).
- [x] Per-slot ring buffer caps at `ceil(window/interval)`-ish — observed 100 frames
  at defaults after ~10 s.
- [x] `!pmres 1` respawns the newest individual death; consumed from stack.
- [x] `!pmres N` where N > stack depth drains everything, reports X/N.
- [x] `!pmevents` — chain-linked grouping works: 3 kills within 1.5 s → one event
  with 3 deaths; add a 4th after 2 s → two events (newest first).
- [x] `!pmresevent <id>` — 3-death event → 3 respawns in one call; stack drains
  exactly that event.
- [x] `!pmreplay <id>` — multi-member event (3 ghosts) spawns and advances on a
  unified timeline; `!pmreplay_status` shows `frame=X/100` progressing.
- [x] `!pmreplaystop` — cancels, `!pmreplay_status` shows `no active replay`.
- [x] Consume-wins: `!pmresevent` during an active replay cancels the replay.
- [x] `EventRoundStart` (via `mp_restartgame 1`) clears the stack AND stops any
  active replay.
- [x] Event-id out of range (`!pmreplay 99`) → polite "no event #99".
- [x] `!pmreplay` on an event whose members all have null `MovementHistory`
  (captured with recording off) → polite deny with explanation.
- [x] Replay of data captured while recording was ON still works after
  `!pmrecording off` (recording-state-at-replay-time is irrelevant).
- [x] Suicide detection: `css_pm_killbot <bot>` (self-suicide) → replay shows no
  killer ghost and no kill-shot beam. Killer-slot == victim-slot guard works.
- [x] `!pmstats` — `deaths=X/Y`, `live buffers=N active`, sampler counters, perf
  tracker, cache misses/heals, interned string count.
- [x] `!pmperfbench` — produces ns/op + p99 µs.
- [x] Perf p99 gates met for tick (target 500 µs, measured ~130 µs). Per-player
  p99 above target (target 20 µs, measured ~45 µs) — documented in FEATURES §6.

---

## Unverified — needs a human client

Test with a real player connected (you on the server via the `connect` ingame
or Steam). Bots show most of the mechanics but these specifically matter:

- [ ] **Green glow companion prop visually pops on the T-side ghost** (yappers'
  recipe — `Glow.GlowColorOverride = Color.Green` + `FollowEntity`). This is
  the bit explicitly asked for. Confirm: during `!pmreplay`, the ghost model
  has a soft green outline visible through walls.
- [ ] **Purple look beam** tracks head direction each frame without flicker.
- [ ] **Red shot beams** flash on fire frames and disappear after their short
  lifetime. Rapid-fire weapons (SMGs) should produce a dense cluster.
- [ ] **Blue kill-shot beam** draws from killer head → victim head on the last
  frame of a non-suicide kill. Only appears once (the `KillShotDrawn` latch).
- [ ] **Kill-shot chat line** ("X killed Y with weapon") prints to chat for all
  players when the kill-shot beam draws. Format + color rendering.
- [ ] **Ghost animation selection** — crouch-vs-stand + knife-vs-rifle picks
  from the four canned anims. Verify visually that a crouch-aiming replay
  plays `crouch_new_rifle_stopped` etc. Note: `tm_phoenix` logs
  `no sequence named:walk_new_rifle_stopped` — expect some models to fall
  back silently (no crash, just no anim). Confirm which player models work
  cleanly on your main JB map.
- [ ] **"prop_dynamic at (0,0,0) has no model name!" warnings are cosmetic** —
  no crashes, replay still runs. (Suppression would require reordering
  `SetModel`/`DispatchSpawn`, which breaks spawn entirely per yappers.)
- [ ] **Replay visible to *all* players, not just the invoker** — CEnvBeam +
  CDynamicProp are server-side entities; every connected client should see
  them. Confirm on a second client or via a spectator.

---

## Unverified — needs a longer session / more players

- [ ] **GC check per FEATURES §6 acceptance** — 5-minute window with
  `!pmrecording on`, 16+ alive, no replays. `dotnet-counters` or
  `GC.CollectionCount(1/2)` before/after should be flat (≤ 1 Gen1 ideally,
  Gen2 = 0). Non-zero Gen1 = string interning leaked or a MovementFrame
  path is boxing; profile.
- [ ] **Full server (32+ alive, real players)** perf — `!pmstats` after a 10-min
  round. Target: `sampler_tick` p99 < 500 µs sustained. If it drifts, lower
  sample rate with `pm_replay_sample_ticks_min 8`.
- [ ] **Cache miss-heal counters** — `!pmstats` should show `misses=0 heals=0`
  in steady state. Non-zero = a CSSharp lifecycle event missed. Surfaces the
  race cases the plan's "heal-on-miss" guard was designed to catch.
- [ ] **FIFO eviction in practice** — lower `pm_max_deaths_stored 32` and keep
  a session going until `!pmstats` reports `evictions > 0`. Verify the oldest
  entries are dropped (the LOG should cite the cap) and replay of newer
  entries still works.
- [ ] **Respawn-then-die within a round** — admin-respawn a bot (externally,
  via `bot_kill` or another plugin), let it die again, check `!pmstack`
  shows two entries for that slot. `!pmresevent` on a grouped event
  containing two entries for the same slot should respawn once, skip the
  second as "already alive".
- [ ] **Disconnect cleanup** — real player dies → disconnects before
  `!pmres`. `!pmstack` should no longer list them (all their entries
  removed). Stats `cache: size` drops by one.
- [ ] **Map change** — `changelevel <map>`. Expected: stack wipes, live buffers
  nulled, intern table cleared. `!pmstats` shows empty state.

---

## Unverified — feature-specific edge cases

- [ ] **Weapon fire stamps shot direction** — open the `!pmstack` frames=N line
  and cross-reference with a replay: the shot-beam frames should line up
  with moments the player fired. (Logging the `ShotDirection` set count per
  replay could be a debug add.)
- [ ] **`EventItemPickup` captured** — pick up a weapon during a recorded
  window; the `DeathEntry.Events` list should include a `WeaponPickup`.
  Currently not surfaced in any command — add to `!pmstack` or
  `!pmreplay_status` if useful.
- [ ] **Damage events captured from the attacker's perspective** — shoot a bot
  (as a real player), let them die; bot's DeathEntry should have
  `DamageTaken` entries, your DeathEntry (if you die later) should have
  `DamageDealt` entries with matching slot/damage.
- [ ] **JB role flags reserved** — `MovementFrame.JbRoleFlags` + the
  `KillerSnapshot` equivalent are byte-reserved and always 0 in v1. Test-ship
  a Jailbreak plugin that sets them on warden/rebel transitions — replay
  should serialize them unchanged (the field is in the record, no wiring
  path exists yet).

---

## What the remote-only test can't answer

Fundamentally:
- Is the glow actually green and not red/blue?
- Is the look beam the right thickness?
- Does the kill-shot line read well in chat?
- Does the replay tell a coherent visual story?

These are all human-judgment questions that bot tests can't answer. Connect and
watch a replay of a bot you just killed yourself — that's the canonical smoke
test.
