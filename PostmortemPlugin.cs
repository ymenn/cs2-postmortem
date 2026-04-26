using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Utils;
using Menn.Utils;
using Microsoft.Extensions.Logging;
using Postmortem.Replay;

namespace Postmortem;

// !sres [N|#id|name] [death|spawn]  — respawn last N / death #id / newest name match
//                                       (default 1, at death-pos when available).
// !sresevent <id>               — respawn everyone in event #id.
// !replay [id|name]             — play back individual death #id / newest name match / newest.
// !replayevent <id>             — play back every member of event #id together.
// !stopreplay                   — cancel active replay.
// !deaths                       — list recent individual deaths with #IDs.
// !devents                      — list death-events (grouped for mass-respawn / mass-replay).
// !fk                           — complain about being freekilled; staff get
//                                 a chat line with the exact !replay command.
// !pmstats                      — storage footprint + sampler counters.
// !pmrecording [on/off/toggle]  — friendly toggle for pm_replay_enabled.
// !pmstack                      — debug, @css/root.
// !pm_killbot <name>            — debug, @css/root.
// css_pmperfbench               — debug, @css/root.
// css_pmreplay_status           — debug, @css/root.
// All chat aliases retain the legacy !pm* spellings for backward compat.
public partial class PostmortemPlugin : BasePlugin
{
    public override string ModuleName => "Postmortem";
    public override string ModuleVersion => "0.3.0";
    public override string ModuleAuthor => "menn";
    public override string ModuleDescription => "Admin tool: respawn + replay the last N deaths.";

    public readonly FakeConVar<float> CvGroupGap = new(
        "pm_group_gap_seconds",
        "Chain-link gap for !pmevents / !pmresevent / !pmreplay grouping (0 = no grouping).",
        1.5f,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<float>(0.0f, 60.0f)
    );

    public readonly FakeConVar<bool> CvReplayEnabled = new(
        "pm_replay_enabled",
        "Master kill-switch for movement sampling + replay. Off = no sampling, no replay.",
        true
    );

    public readonly FakeConVar<float> CvReplayWindow = new(
        "pm_replay_window_seconds",
        "Rolling movement window per player (seconds).",
        10.0f,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<float>(1.0f, 60.0f)
    );

    public readonly FakeConVar<int> CvSampleTicksMin = new(
        "pm_replay_sample_ticks_min",
        "Ticks between samples when <=10 players alive (lower = higher Hz).",
        6,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<int>(1, 32)
    );

    public readonly FakeConVar<int> CvSampleTicksMax = new(
        "pm_replay_sample_ticks_max",
        "Ticks between samples when >40 players alive (lower = higher Hz).",
        10,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<int>(1, 32)
    );

    public readonly FakeConVar<int> CvMaxDeathsStored = new(
        "pm_max_deaths_stored",
        "Safety cap on DeathStack depth. FIFO eviction when exceeded.",
        100,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<int>(32, 2000)
    );

    public readonly FakeConVar<string> CvChatPrefix = new(
        "pm_chat_prefix",
        "Chat-line tag (wrapped in [ ] on output). e.g. 'pm' → [pm].",
        "pm"
    );

    public readonly FakeConVar<float> CvReplayLinger = new(
        "pm_replay_linger_seconds",
        "Seconds to keep replay entities on screen after the last frame plays.",
        5.0f,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<float>(0.0f, 30.0f)
    );

    public readonly FakeConVar<float> CvFkCooldownSeconds = new(
        "pm_fk_cooldown_seconds",
        "Minimum seconds between !fk complaints per player (anti-spam).",
        30.0f,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<float>(0.0f, 600.0f)
    );

    public readonly FakeConVar<int> CvEventAlertMinDeaths = new(
        "pm_event_alert_min_deaths",
        "Minimum chain size to alert staff with !replayevent. 0 = disabled.",
        3,
        ConVarFlags.FCVAR_NONE,
        new RangeValidator<int>(0, 64)
    );

    private readonly DeathStack _stack = new();
    private readonly StringIntern _intern = new();
    private readonly PerfTracker _perf = new();

    // Per-slot last-!fk timestamp for cooldown enforcement. Cleared on
    // disconnect (player slot can be recycled).
    private readonly Dictionary<int, DateTime> _lastFkAt = new();
    private PlayerCache _cache = default!;
    private MovementSampler _sampler = default!;
    private EventRecorder _recorder = default!;
    private MovementReplay? _activeReplay;
    // Pair of fields the playback-end hook reads to print the right respawn
    // command (`!sres #<id>` for single, `!sresevent <id>` for event). Cleared
    // in StopActiveReplay so leftover state doesn't bleed into the next replay.
    private bool _activeReplayIsEvent;
    private DeathEntry? _activeReplaySingleEntry;
    private float _roundStartRealtime;

    // Chain-end staff alert. We don't alert on each death because deaths can
    // keep arriving within `pm_group_gap_seconds` (one event still building);
    // each new death extends the timer (debounce). When the timer fires
    // without further pushes, the chain is closed — if it grew big enough we
    // tell staff. _chainAnchorId is the most recently pushed death's id; the
    // alert sends `!replayevent <anchor>` because FindGroupContainingId walks
    // both directions from any member.
    private int? _chainAnchorId;
    private int _chainCount;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _chainTimer;

    public override void Load(bool hotReload)
    {
        _cache = new PlayerCache(Logger);
        _cache.Start(this, hotReload);

        _sampler = new MovementSampler(
            this,
            _cache,
            _intern,
            _perf,
            Logger,
            enabled: () => CvReplayEnabled.Value,
            windowSeconds: () => CvReplayWindow.Value,
            sampleTicksMin: () => CvSampleTicksMin.Value,
            sampleTicksMax: () => CvSampleTicksMax.Value
        );
        _sampler.Start();
        _sampler.AfterTick = OnSamplerAfterTick;

        // Live-tunable convars: rewire the sampler when values change so admins
        // don't have to wait for the next round. Other convars are read fresh
        // at the call site (CvGroupGap, CvMaxDeathsStored, CvReplayLinger,
        // CvChatPrefix, CvFkCooldownSeconds, CvEventAlertMinDeaths,
        // CvReplayEnabled) and need no hook.
        CvReplayWindow.ValueChanged += (_, _) => _sampler.RebuildBuffers();
        CvSampleTicksMin.ValueChanged += (_, _) => _sampler.RecomputeInterval();
        CvSampleTicksMax.ValueChanged += (_, _) => _sampler.RecomputeInterval();

        _recorder = new EventRecorder(this, _sampler, _intern, () => CvReplayEnabled.Value);
        _recorder.Start();

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventRoundStart>(
            (_, _) =>
            {
                _roundStartRealtime = Server.CurrentTime;
                _stack.Clear();
                _stack.ResetIds();
                ResetChainState();
                StopActiveReplay(reason: "round_start");
                return HookResult.Continue;
            }
        );
        RegisterEventHandler<EventPlayerDisconnect>(
            (@event, _) =>
            {
                var c = @event.Userid;
                if (c is not null && c.IsValid)
                {
                    _stack.Remove(c.Slot);
                    _lastFkAt.Remove(c.Slot);
                }
                return HookResult.Continue;
            }
        );

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _stack.Clear();
            _stack.ResetIds();
            ResetChainState();
            StopActiveReplay(reason: "map_start");
        });

        // Freekill-keyword chat listener. AddCommandListener on say/say_team
        // runs Pre, so we observe the raw message; if the player drops "fk"
        // or "freekill" as a standalone word, route through the same flow as
        // !fk (cooldown + most-recent-death lookup + admin alert). Always
        // returns Continue so we never suppress the actual chat line — the
        // cooldown is what keeps staff chat quiet when the player repeats.
        AddCommandListener("say", OnChatSay, HookMode.Pre);
        AddCommandListener("say_team", OnChatSay, HookMode.Pre);
    }

    public override void Unload(bool hotReload)
    {
        StopActiveReplay(reason: "unload");
        ResetChainState();
        _sampler.AfterTick = null;
        _sampler.Stop();
        _stack.Clear();
        _intern.Clear();
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim is null || !victim.IsValid)
            return HookResult.Continue;

        var victimName = victim.PlayerName ?? $"slot {victim.Slot}";

        MovementFrame[]? frames = null;
        List<ReplayEvent>? events = null;
        KillerSnapshot? killerSnap = null;
        MovementFrame[]? killerFrames = null;

        if (CvReplayEnabled.Value)
        {
            _perf.Measure(
                "replay.death_snapshot",
                () =>
                {
                    var snap = _sampler.SnapshotForDeath(victim.Slot);
                    frames = snap.Frames.Length == 0 ? null : snap.Frames;
                    events = snap.Events;
                    killerSnap = TryBuildKillerSnapshot(@event);

                    // Peek the killer's live buffer (non-destructive — they're
                    // still alive). Skip world/fall kills (no attacker), suicides
                    // (attacker == victim), and disconnect races (invalid).
                    var attacker = @event.Attacker;
                    if (attacker is not null && attacker.IsValid && attacker.Slot != victim.Slot)
                        killerFrames = _sampler.PeekFramesForSlot(attacker.Slot);
                }
            );
        }

        // Captured unconditionally — respawn-at-death is independent of the
        // movement sampler's on/off state.
        Vector? deathPos = null;
        QAngle? deathAngles = null;
        var victimTier = WeaponTier.NotArmed;
        var victimPawn = victim.PlayerPawn?.Value;
        if (victimPawn is not null && victimPawn.IsValid)
        {
            var origin = victimPawn.AbsOrigin;
            var eyes = victimPawn.EyeAngles;
            if (origin is not null)
                deathPos = new Vector(origin.X, origin.Y, origin.Z);
            if (eyes is not null)
                deathAngles = new QAngle(eyes.X, eyes.Y, eyes.Z);
            victimTier = ClassifyVictimWeapons(victimPawn);
        }

        var entry = new DeathEntry(
            Slot: victim.Slot,
            VictimName: victimName,
            At: DateTime.UtcNow,
            MovementHistory: frames,
            Events: events,
            KillerAt: killerSnap,
            DeathPosition: deathPos,
            DeathAngles: deathAngles,
            VictimTier: victimTier,
            KillerMovementHistory: killerFrames,
            VictimTeam: victim.Team
        );
        var pushed = _stack.Push(entry, CvMaxDeathsStored.Value);
        TrackChainAndScheduleAlert(pushed);

        if (_stack.Evictions > 0 && _stack.Count == CvMaxDeathsStored.Value)
        {
            // One-line warning per eviction — if this spams, raise the cap.
            Logger.LogWarning(
                "Postmortem: stack eviction — oldest DeathEntry dropped (cap={Cap} reached, evictions={E}). "
                    + "Raise pm_max_deaths_stored or consume with !pmres/!pmresevent more aggressively.",
                CvMaxDeathsStored.Value,
                _stack.Evictions
            );
        }

        // victimTeam / attackerTeam land in the log so you can later answer
        // "why didn't !sres N pick this entry?" or "did the chain alert count
        // the right deaths?" without re-resolving controllers (which may be
        // gone by the time anyone reads the log).
        var attacker = @event.Attacker;
        var attackerTeam = attacker is not null && attacker.IsValid ? attacker.Team : CsTeam.None;
        Logger.LogInformation(
            "Postmortem: death_push id={Id} slot={Slot} name={Name} victimTeam={VTeam} attackerTeam={ATeam} frames={Frames} killerFrames={KFrames} events={Events} killer={Killer} stackCount={Count}",
            pushed.Id,
            victim.Slot,
            victimName,
            victim.Team,
            attackerTeam,
            frames?.Length ?? 0,
            killerFrames?.Length ?? 0,
            events?.Count ?? 0,
            killerSnap?.KillerName ?? "-",
            _stack.Count
        );
        return HookResult.Continue;
    }

    private KillerSnapshot? TryBuildKillerSnapshot(EventPlayerDeath @event)
    {
        var attacker = @event.Attacker;
        if (attacker is null || !attacker.IsValid)
            return null;
        var pawn = attacker.PlayerPawn?.Value;
        if (pawn is null)
            return null;
        var pos = pawn.AbsOrigin ?? Vector.Zero;
        var rot = pawn.AbsRotation ?? QAngle.Zero;
        var eye = pawn.EyeAngles ?? QAngle.Zero;
        string? modelName = null;
        try
        {
            modelName = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
        }
        catch
        { /* best-effort */
        }
        return new KillerSnapshot(
            KillerSteamId: attacker.SteamID,
            KillerSlot: attacker.Slot,
            KillerName: attacker.PlayerName ?? $"slot {attacker.Slot}",
            KillerPosition: new Vector(pos.X, pos.Y, pos.Z),
            KillerRotation: new QAngle(rot.X, rot.Y, rot.Z),
            KillerViewAngles: new QAngle(eye.X, eye.Y, eye.Z),
            KillerModelName: _intern.Intern(modelName),
            Weapon: _intern.Intern(@event.Weapon) ?? "unknown",
            HitGroup: (byte)@event.Hitgroup,
            Damage: @event.DmgHealth,
            KillerJbRoleFlags: 0
        );
    }

    // Reads the victim's weapon list at death time and reduces it to a single
    // tier (highest wins). Drives the replay ghost tint — see WeaponTier.
    // Best-effort: returns NotArmed if WeaponServices isn't reachable (rare
    // disconnect race at death).
    private static WeaponTier ClassifyVictimWeapons(CCSPlayerPawn pawn)
    {
        var ws = pawn.WeaponServices;
        if (ws is null) return WeaponTier.NotArmed;
        var tier = WeaponTier.NotArmed;
        foreach (var handle in ws.MyWeapons)
        {
            var w = handle.Value;
            if (w is null || !w.IsValid) continue;
            var dn = w.DesignerName;
            if (string.IsNullOrEmpty(dn)) continue;
            var t = ClassifyDesignerName(dn);
            if (t > tier) tier = t;
            if (tier == WeaponTier.Primary) break; // can't go higher
        }
        return tier;
    }

    private static WeaponTier ClassifyDesignerName(string designerName)
    {
        // Strip "weapon_" prefix once so the switch keys stay short.
        var name = designerName.StartsWith("weapon_") ? designerName[7..] : designerName;
        return name switch
        {
            // Rifles, snipers, SMGs, shotguns, MGs — anything you'd call a "gun"
            // for fighting purposes counts as Primary.
            "ak47" or "m4a1" or "m4a1_silencer" or "famas" or "galilar"
              or "aug" or "sg556"
              or "awp" or "ssg08" or "scar20" or "g3sg1"
              or "mp9" or "mac10" or "mp7" or "mp5sd" or "ump45" or "p90" or "bizon"
              or "nova" or "xm1014" or "sawedoff" or "mag7"
              or "negev" or "m249" => WeaponTier.Primary,
            // Pistols + zeus (one-shot kill counts as armed).
            "glock" or "usp_silencer" or "hkp2000" or "p250" or "deagle"
              or "revolver" or "fiveseven" or "tec9" or "elite" or "cz75a"
              or "taser" => WeaponTier.Pistol,
            // Knives, grenades, kit, c4 → utility = NotArmed for tier purposes.
            _ => WeaponTier.NotArmed,
        };
    }

    private void OnSamplerAfterTick()
    {
        if (_activeReplay is null)
            return;
        try
        {
            _activeReplay.Tick();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Postmortem: replay tick threw; stopping replay");
            StopActiveReplay(reason: "error");
            return;
        }
        if (!_activeReplay.IsPlaying)
        {
            AnnounceReplayEnded();
            StopActiveReplay(reason: "playback_end");
        }
    }

    // Tell admins the replay is done and what to type to revive. Only fires
    // on natural end (playback_end) — manual_stop / round_start / consumed /
    // unload paths skip this so we don't spam staff with stale prompts.
    private void AnnounceReplayEnded()
    {
        if (_activeReplay is null) return;
        string localized;
        if (_activeReplayIsEvent)
        {
            var cmd = $"{ChatColors.LightYellow}!sresevent {_activeReplay.EventId}{ChatColors.Default}";
            localized = Localizer["pm.replayevent.ended", cmd];
        }
        else if (_activeReplaySingleEntry is { } entry)
        {
            var cmd = $"{ChatColors.LightYellow}!sres #{entry.Id}{ChatColors.Default}";
            localized = Localizer["pm.replay.ended", entry.VictimName, cmd];
        }
        else
        {
            return;
        }
        var line = $"{ChatPrefixColored} {ChatColors.Green}{localized}{ChatColors.Default}";
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot) continue;
            if (!AdminManager.PlayerHasPermissions(p, "@css/generic")) continue;
            p.PrintToChat(line);
        }
    }

    private void StopActiveReplay(string reason)
    {
        if (_activeReplay is null)
            return;
        Logger.LogInformation(
            "Postmortem: replay_stop reason={Reason} eventId={Id}",
            reason,
            _activeReplay.EventId
        );
        _activeReplay.Stop();
        _activeReplay = null;
        _activeReplayIsEvent = false;
        _activeReplaySingleEntry = null;
    }

    // ===== Commands =====

    [ConsoleCommand("css_sres", "Respawn the last N dead, the newest <name>, or death #id.")]
    [ConsoleCommand("css_pmres", "Alias of !sres.")]
    [CommandHelper(
        minArgs: 0,
        usage: "[count|#id|name] [spawn|death]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER
    )]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmRes(CCSPlayerController? caller, CommandInfo info)
    {
        var count = 1;
        string? nameNeedle = null;
        int? deathIdTarget = null;
        var whereArgIndex = 2;
        if (info.ArgCount >= 2)
        {
            var arg1 = info.GetArg(1);
            if (arg1.StartsWith("#"))
            {
                // Specific death-id form: `!sres #3` — pop the entry at
                // newest-first index 3 (matches the #N from !deaths).
                if (!int.TryParse(arg1.AsSpan(1), out var idVal) || idVal <= 0)
                {
                    Reply(caller, info, ChatColors.Red, Localizer["pm.res.bad_id"]);
                    return;
                }
                deathIdTarget = idVal;
            }
            else if (int.TryParse(arg1, out var parsed))
            {
                if (parsed <= 0)
                {
                    Reply(caller, info, ChatColors.Red, Localizer["pm.res.bad_count"]);
                    return;
                }
                count = parsed;
            }
            else if (IsRespawnWhereKeyword(arg1))
            {
                // Location-only form: `!pmres death` — count stays 1, parse
                // the where keyword from arg index 1 instead of 2.
                whereArgIndex = 1;
            }
            else
            {
                nameNeedle = arg1;
            }
        }
        if (!TryParseRespawnWhere(info, whereArgIndex, out var atDeath, caller))
            return;

        IReadOnlyList<DeathEntry> popped;
        if (deathIdTarget is { } targetId)
        {
            var entry = _stack.PopById(targetId);
            if (entry is null)
            {
                Reply(caller, info, ChatColors.Yellow, Localizer["pm.replay.no_id", targetId]);
                return;
            }
            popped = new[] { entry };
        }
        else if (nameNeedle is not null)
        {
            var snap = _stack.SnapshotNewestFirst();
            var match = TryFindDeathByName(snap, nameNeedle);
            if (match is null)
            {
                Reply(caller, info, ChatColors.Yellow, Localizer["pm.res.no_name_match", nameNeedle]);
                return;
            }
            var entry = _stack.PopById(match.Id);
            popped = entry is null ? Array.Empty<DeathEntry>() : new[] { entry };
        }
        else
        {
            // T-only: a CT slaying themselves as freekill punishment shouldn't
            // burn one of the N slots the admin asked for. Internal CT
            // incidents are revived via explicit `!sres #id`.
            popped = _stack.PopLastNTerrorist(count);
        }
        if (popped.Count == 0)
        {
            Reply(caller, info, ChatColors.Grey, Localizer["pm.res.empty"]);
            return;
        }

        var names = new List<string>(popped.Count);
        var skipped = 0;
        foreach (var e in popped)
        {
            var c = Utilities.GetPlayerFromSlot(e.Slot);
            if (c is null || !c.IsValid)
            {
                skipped++;
                continue;
            }
            if (c.PawnIsAlive)
            {
                skipped++;
                continue;
            }
            if (c.Team != CsTeam.Terrorist && c.Team != CsTeam.CounterTerrorist)
            {
                skipped++;
                continue;
            }
            RespawnAt(c, e, atDeath);
            names.Add(c.PlayerName ?? $"slot {e.Slot}");
        }

        // If we consumed entries that belong to the active replay, cancel it
        // (consume-wins per plan).
        MaybeCancelReplayForEntries(popped);

        if (names.Count == 0)
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.res.partial", popped.Count]);
        else
            Reply(
                caller,
                info,
                ChatColors.Green,
                Localizer["pm.res.ok", names.Count, popped.Count, string.Join(", ", names)]
            );
        Logger.LogInformation(
            "Postmortem: pmres popped={Popped} respawned={Respawned} skipped={Skipped} requested={Req} atDeath={At}",
            popped.Count,
            names.Count,
            skipped,
            count,
            atDeath
        );
    }

    [ConsoleCommand("css_devents", "List recent death-events with #IDs.")]
    [ConsoleCommand("css_pmevents", "Alias of !devents.")]
    [ConsoleCommand("css_pmev", "Alias of !devents.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmEvents(CCSPlayerController? caller, CommandInfo info)
    {
        var gap = CvGroupGap.Value;
        var groups = _stack.SnapshotGroups(gap);
        if (groups.Count == 0)
        {
            Reply(caller, info, ChatColors.Grey, Localizer["pm.events.empty"]);
            return;
        }

        var now = DateTime.UtcNow;
        var lines = new List<string>
        {
            Localizer["pm.events.header", groups.Count, gap.ToString("F1")],
        };
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            // newest → oldest within the group; names in chronological order.
            var victimNames = new List<string>(g.Count);
            for (var k = g.Count - 1; k >= 0; k--)
                victimNames.Add(ColorName(g[k].VictimName));
            var newestAt = g[0].At;
            var oldestAt = g[^1].At;
            var ago = FormatAgo(now - newestAt);
            var victimsLabel = string.Join(", ", victimNames);
            // Event id = the anchor (newest) death's stable id. Stable for as
            // long as the event is in the stack — typing an id won't shift
            // when a new death pushes onto the top.
            var eventId = g[0].Id;
            if (g.Count == 1)
                lines.Add(Localizer["pm.events.row_one", eventId, ago, victimsLabel]);
            else
            {
                var duration = (newestAt - oldestAt).TotalSeconds.ToString("F1");
                lines.Add(
                    Localizer["pm.events.row_many", eventId, ago, g.Count, duration, victimsLabel]
                );
            }
        }
        ReplyLines(caller, info, "events", lines);
    }

    [ConsoleCommand("css_sresevent", "Respawn everyone in event #id (default: newest event).")]
    [ConsoleCommand("css_pmresevent", "Alias of !sresevent.")]
    [ConsoleCommand("css_pmre", "Alias of !sresevent.")]
    [CommandHelper(
        minArgs: 0,
        usage: "[event_id] [spawn|death]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER
    )]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmResEvent(CCSPlayerController? caller, CommandInfo info)
    {
        // Resolve target event: explicit id (numeric arg1), default to newest
        // T-team event when arg1 is missing or a `[spawn|death]` keyword.
        // Defaulting to the newest T death (not the absolute newest) skips a
        // trailing CT self-slay so `!sresevent` lands on the freekill chain.
        int id;
        var whereArgIndex = 2;
        if (info.ArgCount >= 2 && int.TryParse(info.GetArg(1), out var parsed) && parsed > 0)
        {
            id = parsed;
        }
        else
        {
            var newestT = _stack.NewestTerroristDeathId();
            if (newestT is null)
            {
                Reply(caller, info, ChatColors.Grey, Localizer["pm.events.empty"]);
                return;
            }
            id = newestT.Value;
            // arg1 may carry a location keyword when no id was given.
            if (info.ArgCount >= 2) whereArgIndex = 1;
        }
        if (!TryParseRespawnWhere(info, whereArgIndex, out var atDeath, caller))
            return;

        // Pop only the T members of the chain. CT entries (e.g. the killer
        // self-slaying) stay in the stack so they remain replayable / can be
        // revived individually via `!sres #id` if needed.
        var popped = _stack.PopGroupTerroristContainingId(id, CvGroupGap.Value);
        if (popped.Count == 0)
        {
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.event.none", id]);
            return;
        }

        var names = new List<string>(popped.Count);
        foreach (var e in popped)
        {
            var c = Utilities.GetPlayerFromSlot(e.Slot);
            if (c is null || !c.IsValid)
                continue;
            if (c.PawnIsAlive)
                continue;
            if (c.Team != CsTeam.Terrorist && c.Team != CsTeam.CounterTerrorist)
                continue;
            RespawnAt(c, e, atDeath);
            names.Add(c.PlayerName ?? $"slot {e.Slot}");
        }

        MaybeCancelReplayForEntries(popped);

        if (names.Count == 0)
            Reply(
                caller,
                info,
                ChatColors.Yellow,
                Localizer["pm.resevent.partial", popped.Count, id]
            );
        else
            Reply(
                caller,
                info,
                ChatColors.Green,
                Localizer["pm.resevent.ok", names.Count, popped.Count, id, string.Join(", ", names)]
            );
        Logger.LogInformation(
            "Postmortem: pmresevent id={Id} popped={Popped} respawned={R} atDeath={At}",
            id,
            popped.Count,
            names.Count,
            atDeath
        );
    }

    // True when `s` is one of the location keywords accepted by
    // TryParseRespawnWhere — used by !pmres to disambiguate `!pmres death`
    // (location-only) from `!pmres <name>` (name search).
    private static bool IsRespawnWhereKeyword(string s) => s.ToLowerInvariant() switch
    {
        "spawn" or "team" or "death" or "here" or "at" => true,
        _ => false,
    };

    // Parses the optional `spawn|death` trailing arg shared by !sres and
    // !sresevent. Default is death-pos when available — RespawnAt falls back
    // to team spawn automatically when DeathPosition is null. Returns true
    // when the arg is absent or valid (atDeath set); false + error message
    // when invalid.
    private bool TryParseRespawnWhere(
        CommandInfo info,
        int argIndex,
        out bool atDeath,
        CCSPlayerController? caller
    )
    {
        atDeath = true;
        if (info.ArgCount <= argIndex)
            return true;
        var arg = info.GetArg(argIndex).ToLowerInvariant();
        switch (arg)
        {
            case "spawn":
            case "team":
                atDeath = false;
                return true;
            case "death":
            case "here":
            case "at":
                atDeath = true;
                return true;
            default:
                Reply(caller, info, ChatColors.Red, Localizer["pm.res.bad_where"]);
                return false;
        }
    }

    // Engine-picked team spawn, or the death position if the caller asked for
    // `death` and we have one captured. Respawn is asynchronous — the pawn is
    // revived on the next frame, so the teleport has to defer to then too.
    private void RespawnAt(CCSPlayerController c, DeathEntry entry, bool atDeath)
    {
        c.Respawn();
        if (!atDeath || entry.DeathPosition is null)
            return;
        var pos = entry.DeathPosition;
        // Body yaw only — DeathAngles comes from EyeAngles which carries
        // pitch (look up/down) and sometimes roll. Feeding those into pawn
        // Teleport bends the model. Y is the heading the player was facing;
        // that's all the body needs.
        var bodyYaw = new QAngle(0f, (entry.DeathAngles ?? QAngle.Zero).Y, 0f);
        Server.NextFrame(() =>
        {
            if (!c.IsValid)
                return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn is null || !pawn.IsValid)
                return;
            pawn.Teleport(pos, bodyYaw, Vector.Zero);
        });
    }

    [ConsoleCommand("css_deaths", "List recent individual deaths with #IDs.")]
    [ConsoleCommand("css_pmdeaths", "Alias of !deaths.")]
    [ConsoleCommand("css_pmd", "Alias of !deaths.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmDeaths(CCSPlayerController? caller, CommandInfo info)
    {
        var snap = _stack.SnapshotNewestFirst();
        if (snap.Count == 0)
        {
            Reply(caller, info, ChatColors.Grey, Localizer["pm.deaths.empty"]);
            return;
        }

        var now = DateTime.UtcNow;
        var lines = new List<string> { Localizer["pm.deaths.header", snap.Count] };
        for (var i = 0; i < snap.Count; i++)
        {
            var e = snap[i];
            var id = e.Id;
            var ago = FormatAgo(now - e.At);
            var killer = e.KillerAt;
            var victim = ColorName(e.VictimName);
            string line;
            if (killer is null)
                line = Localizer["pm.deaths.row", id, ago, victim];
            else if (killer.KillerSlot == e.Slot)
                line = Localizer["pm.deaths.row_suicide", id, ago, victim];
            else
                line = Localizer["pm.deaths.row_kill", id, ago, victim, ColorKillerName(killer.KillerName)];
            lines.Add(line);
        }
        ReplyLines(caller, info, "deaths", lines);
    }

    [ConsoleCommand(
        "css_replay",
        "Play back individual death #id or the newest death matching <name>."
    )]
    [ConsoleCommand("css_pmreplay", "Alias of !replay.")]
    [ConsoleCommand("css_pmr", "Alias of !replay.")]
    [ConsoleCommand("css_var", "Alias of !replay.")]
    [CommandHelper(
        minArgs: 0,
        usage: "[death_id|name]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER
    )]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmReplay(CCSPlayerController? caller, CommandInfo info)
    {
        var snap = _stack.SnapshotNewestFirst();

        // Resolve the individual death to play: by explicit stable id, by name
        // substring (newest match wins), or default to the newest death.
        DeathEntry? entry = null;
        int requestedId = 0;
        if (info.ArgCount < 2)
        {
            if (snap.Count > 0) entry = snap[0];
        }
        else if (int.TryParse(info.GetArg(1), out var parsedId) && parsedId > 0)
        {
            requestedId = parsedId;
            entry = _stack.FindById(parsedId);
        }
        else
        {
            var needle = info.GetArg(1);
            entry = TryFindDeathByName(snap, needle);
            if (entry is null)
            {
                Reply(
                    caller,
                    info,
                    ChatColors.Yellow,
                    Localizer["pm.replay.no_name_match", needle]
                );
                return;
            }
        }

        if (entry is null)
        {
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.replay.no_id", requestedId]);
            return;
        }
        var id = entry.Id;
        if (entry.MovementHistory is null || entry.MovementHistory.Length == 0)
        {
            // MovementHistory is null/empty when pm_replay_enabled was off at
            // the time of death, or the sampler hadn't captured frames yet
            // (e.g. victim spawned <1 tick-interval before dying).
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.replay.no_data", id]);
            return;
        }

        var members = new[] { entry };
        StopActiveReplay(reason: "new_replay");
        _activeReplay = new MovementReplay(
            id,
            members,
            Logger,
            FormatReplayKillLine,
            lingerSeconds: () => CvReplayLinger.Value,
            formatShotLine: FormatReplayShotLine
        );
        _activeReplayIsEvent = false;
        _activeReplaySingleEntry = entry;
        var windowSec = MaxWindowSeconds(members);
        Reply(
            caller,
            info,
            ChatColors.Green,
            Localizer["pm.replay.started", id, entry.VictimName, windowSec.ToString("F1")]
        );
        Logger.LogInformation(
            "Postmortem: replay_start deathId={Id} victim={V}",
            id,
            entry.VictimName
        );
    }

    [ConsoleCommand("css_replayevent", "Replay all members of event #id together (default: newest event).")]
    [ConsoleCommand("css_replayev", "Alias of !replayevent.")]
    [ConsoleCommand("css_pmreplayevent", "Alias of !replayevent.")]
    [ConsoleCommand("css_pmrev", "Alias of !replayevent.")]
    [ConsoleCommand("css_varevent", "Alias of !replayevent.")]
    [ConsoleCommand("css_varev", "Alias of !replayevent.")]
    [CommandHelper(minArgs: 0, usage: "[event_id]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmReplayEvent(CCSPlayerController? caller, CommandInfo info)
    {
        int id;
        if (info.ArgCount >= 2)
        {
            if (!int.TryParse(info.GetArg(1), out id) || id <= 0)
            {
                Reply(caller, info, ChatColors.Red, Localizer["pm.event.bad_id"]);
                return;
            }
        }
        else
        {
            var snap = _stack.SnapshotNewestFirst();
            if (snap.Count == 0)
            {
                Reply(caller, info, ChatColors.Grey, Localizer["pm.events.empty"]);
                return;
            }
            id = snap[0].Id;
        }
        var group = _stack.FindGroupContainingId(id, CvGroupGap.Value);
        if (group.Count == 0)
        {
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.event.none", id]);
            return;
        }
        // MovementReplay drops members with empty histories internally, but
        // we want a clean "no data" reply if the whole group has nothing.
        var members = new List<DeathEntry>(group.Count);
        foreach (var e in group)
        {
            if (e.MovementHistory is not null && e.MovementHistory.Length > 0)
                members.Add(e);
        }
        if (members.Count == 0)
        {
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.replayevent.no_data", id]);
            return;
        }

        StopActiveReplay(reason: "new_replay");
        _activeReplay = new MovementReplay(
            id,
            members,
            Logger,
            FormatReplayKillLine,
            lingerSeconds: () => CvReplayLinger.Value,
            formatShotLine: FormatReplayShotLine
        );
        _activeReplayIsEvent = true;
        _activeReplaySingleEntry = null;
        var windowSec = MaxWindowSeconds(members);
        var victimsLabel = string.Join(", ", members.ConvertAll(m => m.VictimName));
        Reply(
            caller,
            info,
            ChatColors.Green,
            Localizer["pm.replayevent.started", id, members.Count, victimsLabel, windowSec.ToString("F1")]
        );
        Logger.LogInformation(
            "Postmortem: replay_event_start eventId={Id} members={M}",
            id,
            members.Count
        );
    }

    // Newest-first match of `needle` against victim names. Returns the matching
    // entry (with its stable Id) or null. Caller uses entry.Id for any
    // subsequent stack lookup.
    private static DeathEntry? TryFindDeathByName(
        IReadOnlyList<DeathEntry> snapNewestFirst,
        string needle
    )
    {
        if (string.IsNullOrWhiteSpace(needle)) return null;
        foreach (var e in snapNewestFirst)
        {
            if (e.VictimName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    [ConsoleCommand(
        "css_fk",
        "Flag the caller's most recent death as a freekill; staff get the replay command in chat."
    )]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnCommandFk(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller is null || !caller.IsValid)
            return;
        HandleFkComplaint(caller, replyToCommand: info);
    }

    // Shared complaint flow, called from both the !fk command and the chat
    // listener when a player types "fk" / "freekill" as a standalone word.
    // `replyToCommand` carries through so an explicit command gets a reply;
    // when triggered by a chat keyword, the only user-facing feedback is the
    // staff alert (the player sees their own chat message echo, plus — on
    // success — the "staff notified" ack printed to them directly).
    private void HandleFkComplaint(CCSPlayerController caller, CommandInfo? replyToCommand)
    {
        // Freekill complaints are only meaningful immediately after dying —
        // a living player typing !fk (or "fk" in chat) is either trolling or
        // confused. Silent for chat-keyword triggers so we don't teach them
        // they're being filtered; explicit !fk gets a one-line nudge.
        if (caller.PawnIsAlive)
        {
            if (replyToCommand is not null)
                Reply(caller, replyToCommand, ChatColors.Grey, Localizer["pm.fk.alive"]);
            return;
        }

        // Cooldown — prevents a single player from nuking staff chat.
        // Applies even when the user didn't type the command (keyword
        // detection feeds through here too), so repeated "fk fk fk" in chat
        // results in one alert, not N.
        if (_lastFkAt.TryGetValue(caller.Slot, out var last))
        {
            var elapsed = (DateTime.UtcNow - last).TotalSeconds;
            var cooldown = CvFkCooldownSeconds.Value;
            if (elapsed < cooldown)
            {
                // Silent cooldown when triggered by chat keyword — players
                // shouldn't learn they're being filtered. Explicit command
                // callers do get told.
                if (replyToCommand is not null)
                    Reply(
                        caller,
                        replyToCommand,
                        ChatColors.Yellow,
                        Localizer["pm.fk.cooldown", (int)Math.Ceiling(cooldown - elapsed)]
                    );
                return;
            }
        }

        // Find the caller's most recent death in the stack; its stable Id is
        // what `!replay <id>` takes (and stays stable as new deaths push).
        var snap = _stack.SnapshotNewestFirst();
        var deathId = 0;
        DeathEntry? entry = null;
        foreach (var e in snap)
        {
            if (e.Slot == caller.Slot)
            {
                deathId = e.Id;
                entry = e;
                break;
            }
        }
        if (entry is null)
        {
            if (replyToCommand is not null)
                Reply(caller, replyToCommand, ChatColors.Grey, Localizer["pm.fk.no_death"]);
            return;
        }

        _lastFkAt[caller.Slot] = DateTime.UtcNow;

        var victimName = caller.PlayerName ?? entry.VictimName;
        var killer = entry.KillerAt;
        // The replay command is the actionable bit of the alert — colour it
        // separately from the red urgency text so it's easy to spot and
        // click-to-copy. Switch back to red after so any localizer suffix
        // (currently none — command is always at end) keeps reading red.
        var cmd = $"{ChatColors.LightYellow}!replay {deathId}{ChatColors.Red}";
        string alert;
        if (killer is null || killer.KillerSlot == entry.Slot)
            alert = Localizer["pm.fk.alert_no_killer", victimName, cmd];
        else
            alert = Localizer["pm.fk.alert_kill", victimName, killer.KillerName, cmd];
        var alertLine = $"{ChatPrefixColored} {ChatColors.Red}{alert}{ChatColors.Default}";

        var notified = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot)
                continue;
            if (!AdminManager.PlayerHasPermissions(p, "@css/generic"))
                continue;
            p.PrintToChat(alertLine);
            notified++;
        }

        // Always ack the caller — whether they used !fk or just typed it in
        // chat — so they know the complaint reached staff.
        if (replyToCommand is not null)
            Reply(caller, replyToCommand, ChatColors.Green, Localizer["pm.fk.thanks", notified]);
        else
            caller.PrintToChat(
                $"{ChatPrefixColored} {ChatColors.Green}{Localizer["pm.fk.thanks", notified]}{ChatColors.Default}"
            );

        Logger.LogInformation(
            "Postmortem: fk_complaint caller={Caller} victimTeam={VTeam} deathId={Id} killer={Killer} notifiedAdmins={N}",
            victimName,
            entry.VictimTeam,
            deathId,
            killer?.KillerName ?? "-",
            notified
        );
    }

    private HookResult OnChatSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player is null || !player.IsValid || player.IsBot)
            return HookResult.Continue;
        var message = info.GetArg(1);
        if (string.IsNullOrEmpty(message))
            return HookResult.Continue;
        // Chat-command prefixes are routed by CSSharp's command dispatcher —
        // !fk already goes through OnCommandFk. Skip keyword scanning so a
        // user typing !fk doesn't double-trigger.
        if (message[0] is '!' or '/' or '.')
            return HookResult.Continue;
        if (!IsFreekillKeyword(message))
            return HookResult.Continue;
        HandleFkComplaint(player, replyToCommand: null);
        return HookResult.Continue;
    }

    // Token-match against "fk" / "freekill" so "fk that cooked me" fires but
    // "luck" or "talk" don't. Case-insensitive.
    private static bool IsFreekillKeyword(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg))
            return false;
        foreach (
            var token in msg.Split(
                new[] { ' ', '\t', ',', '.', '!', '?' },
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            if (string.Equals(token, "fk", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(token, "freekill", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    [ConsoleCommand("css_stopreplay", "Cancel the active replay.")]
    [ConsoleCommand("css_stopr", "Alias of !stopreplay.")]
    [ConsoleCommand("css_pmreplaystop", "Alias of !stopreplay.")]
    [ConsoleCommand("css_pmrs", "Alias of !stopreplay.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmReplayStop(CCSPlayerController? caller, CommandInfo info)
    {
        if (_activeReplay is null)
        {
            Reply(caller, info, ChatColors.Grey, Localizer["pm.replaystop.none"]);
            return;
        }
        var id = _activeReplay.EventId;
        StopActiveReplay(reason: "manual_stop");
        Reply(caller, info, ChatColors.Green, Localizer["pm.replaystop.ok", id]);
    }

    // Called from MovementReplay when the kill shot hits — localizes the chat
    // line so replay announcements match the caller's language. `elapsed` is
    // seconds since replay started, prepended as a `[+X.Xs]` tag so admins
    // reading the chat can correlate actions to playback time.
    private string FormatReplayKillLine(float elapsed, string killer, string victim, string weapon) =>
        $"{ChatPrefixColored} {FormatReplayTimeTag(elapsed)} {Localizer["pm.replay.killshot", killer, victim, weapon]}";

    // Called from MovementReplay when the T switches weapon during playback.
    // Branches by weapon-name category (melee / grenade / firearm) to pick the
    // right verb. `weapon` is a CS2 designer name ("weapon_ak47",
    // "weapon_hegrenade", "weapon_knife_karambit", etc.). Strip the "weapon_"
    // prefix for display — keeps the chat line readable without a full
    // humanisation table.
    private string FormatReplayShotLine(float elapsed, string actor, string weapon)
    {
        var label = weapon.StartsWith("weapon_", StringComparison.Ordinal)
            ? weapon["weapon_".Length..]
            : weapon;
        string key;
        if (IsMeleeWeapon(weapon))
            key = "pm.replay.swung_knife";
        else if (IsGrenade(weapon))
            key = "pm.replay.threw";
        else
            key = "pm.replay.fired";
        return $"{ChatPrefixColored} {FormatReplayTimeTag(elapsed)} {Localizer[key, actor, label]}";
    }

    private static string FormatReplayTimeTag(float elapsed) =>
        $"{ChatColors.Grey}[+{elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s]{ChatColors.Default}";

    private static bool IsMeleeWeapon(string designerName) =>
        designerName.Contains("knife", StringComparison.Ordinal)
        || designerName.Contains("bayonet", StringComparison.Ordinal);

    private static bool IsGrenade(string designerName) =>
        designerName.Contains("grenade", StringComparison.Ordinal)
        || designerName.Contains("molotov", StringComparison.Ordinal)
        || designerName.Contains("flashbang", StringComparison.Ordinal)
        || designerName.Contains("decoy", StringComparison.Ordinal)
        || designerName.Contains("incgrenade", StringComparison.Ordinal);

    [ConsoleCommand("css_pmstats", "Storage footprint + sampler counters.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmStats(CCSPlayerController? caller, CommandInfo info)
    {
        var stackCount = _stack.Count;
        var cap = CvMaxDeathsStored.Value;
        var liveStats = _sampler.LiveBufferStats();
        long stackFrames = 0;
        foreach (var e in _stack.SnapshotNewestFirst())
            stackFrames += e.MovementHistory?.Length ?? 0;

        const int frameBytes = 72; // rough per-reference size incl. two strings + two Vectors + QAngles
        var stackKb = stackFrames * frameBytes / 1024;
        var liveKb = liveStats.TotalFrames * frameBytes / 1024;

        var lines = new List<string>
        {
            $"replay={(CvReplayEnabled.Value ? "ON" : "OFF")} | deaths={stackCount}/{cap} | live buffers={liveStats.LiveBuffers} active",
            $"mem: stack snapshots ~{stackKb} KB, live buffers ~{liveKb} KB, peak deaths this session={_stack.PeakCount}",
            $"sampler: tick_count={_sampler.TickCount} samples={_sampler.SamplesTaken} interval={_sampler.CurrentIntervalTicks}ticks evictions={_stack.Evictions}",
            $"cache: size={_cache.Count} misses={_cache.MissCount} heals={_cache.HealCount} | interned_strings={_intern.Count}",
        };
        ReplyLines(caller, info, "stats", lines);
    }

    [ConsoleCommand("css_pmrecording", "Toggle pm_replay_enabled (on|off|toggle).")]
    [ConsoleCommand("css_pmrec", "Alias of !pmrecording.")]
    [CommandHelper(
        minArgs: 0,
        usage: "[on|off|toggle]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER
    )]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmRecording(CCSPlayerController? caller, CommandInfo info)
    {
        var arg = info.ArgCount >= 2 ? info.GetArg(1).ToLowerInvariant() : "toggle";
        bool newState = CvReplayEnabled.Value;
        switch (arg)
        {
            case "on":
                newState = true;
                break;
            case "off":
                newState = false;
                break;
            case "toggle":
                newState = !CvReplayEnabled.Value;
                break;
            default:
                Reply(caller, info, ChatColors.Red, Localizer["pm.recording.bad_arg"]);
                return;
        }
        CvReplayEnabled.Value = newState;
        Reply(
            caller,
            info,
            newState ? ChatColors.Green : ChatColors.Yellow,
            Localizer[newState ? "pm.recording.on" : "pm.recording.off"]
        );
        Logger.LogInformation("Postmortem: pm_replay_enabled set to {State}", newState);
    }

    [ConsoleCommand("css_pmstack", "DEBUG: dump current death stack with ages + group boundaries.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandPmStack(CCSPlayerController? caller, CommandInfo info)
    {
        var snap = _stack.SnapshotNewestFirst();
        if (snap.Count == 0)
        {
            info.ReplyToCommand($"{ChatPrefixPlain} stack empty");
            return;
        }

        var gap = CvGroupGap.Value;
        var now = DateTime.UtcNow;
        var lines = new List<string> { $"stack ({snap.Count}), gap<={gap:F1}s:" };
        var groupIdx = 1;
        DateTime? anchor = null;
        for (var i = 0; i < snap.Count; i++)
        {
            var e = snap[i];
            if (anchor is null)
                anchor = e.At;
            else if ((anchor.Value - e.At).TotalSeconds > gap)
            {
                lines.Add("  ---");
                groupIdx++;
                anchor = e.At;
            }
            else
                anchor = e.At;

            var c = Utilities.GetPlayerFromSlot(e.Slot);
            var name = c?.PlayerName ?? e.VictimName;
            var alive = c is not null && c.IsValid && c.PawnIsAlive ? "alive" : "dead";
            var age = (now - e.At).TotalSeconds;
            var frames = e.MovementHistory?.Length ?? 0;
            var kframes = e.KillerMovementHistory?.Length ?? 0;
            var events = e.Events?.Count ?? 0;
            var killer = e.KillerAt?.KillerName ?? "-";
            var pos = e.DeathPosition is { } p ? $"({p.X:F0},{p.Y:F0},{p.Z:F0})" : "-";
            lines.Add(
                $"  g{groupIdx} #{e.Id} slot={e.Slot} name={name} age={age:F1}s ({alive}) frames={frames} kframes={kframes} events={events} killer={killer} deathpos={pos}"
            );
        }
        ReplyLines(caller, info, "stack", lines);
    }

    [ConsoleCommand("css_pm_killbot", "DEBUG: force-kill a bot by name.")]
    [ConsoleCommand("css_pmkb", "DEBUG: alias of !pm_killbot.")]
    [CommandHelper(minArgs: 1, usage: "<bot_name>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandKillBot(CCSPlayerController? caller, CommandInfo info)
    {
        var name = info.GetArg(1);
        foreach (var c in Utilities.GetPlayers())
        {
            if (c is null || !c.IsValid || !c.IsBot)
                continue;
            if (!string.Equals(c.PlayerName, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!c.PawnIsAlive)
            {
                info.ReplyToCommand($"{ChatPrefixPlain} {name} already dead");
                return;
            }
            c.CommitSuicide(explode: false, force: true);
            info.ReplyToCommand($"{ChatPrefixPlain} killed {name} (slot {c.Slot})");
            return;
        }
        info.ReplyToCommand($"{ChatPrefixPlain} bot '{name}' not found");
    }

    [ConsoleCommand("css_pmperfbench", "DEBUG: run 10,000 synthetic sampler ops and report ns/op.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandPmPerfBench(CCSPlayerController? caller, CommandInfo info)
    {
        var players = new List<CCSPlayerController>();
        foreach (var entry in _cache.AliveCombatants())
            players.Add(entry.Controller);

        var iterations = 10_000;
        var sw = Stopwatch.StartNew();
        var samplesTaken = 0;
        for (var i = 0; i < iterations; i++)
        {
            if (players.Count == 0)
            {
                // No-op workload — still exercises PerfTracker plumbing.
                _perf.Measure(
                    "replay.bench.nop",
                    () =>
                    {
                        samplesTaken++;
                    }
                );
                continue;
            }
            var p = players[i % players.Count];
            _perf.Measure(
                "replay.bench.one_player",
                () =>
                {
                    var pawn = p.PlayerPawn?.Value;
                    if (pawn is null)
                        return;
                    var _loc = pawn.AbsOrigin ?? Vector.Zero;
                    var _eye = pawn.EyeAngles ?? QAngle.Zero;
                    var _rot = pawn.AbsRotation ?? QAngle.Zero;
                    var _vel = pawn.AbsVelocity ?? Vector.Zero;
                    var _dn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                    samplesTaken++;
                }
            );
        }
        sw.Stop();

        var opName = players.Count == 0 ? "replay.bench.nop" : "replay.bench.one_player";
        var stats = _perf.Snapshot(opName);
        var nsPerOp = sw.Elapsed.TotalNanoseconds / iterations;
        var lines = new List<string>
        {
            $"bench: iterations={iterations} players={players.Count} total={sw.ElapsedMilliseconds} ms  ~{nsPerOp:F0} ns/op",
        };
        if (stats is not null)
            lines.Add(
                $"  op={stats.OpName} n={stats.Count} p50={stats.P50Micros}µs p95={stats.P95Micros}µs p99={stats.P99Micros}µs max={stats.MaxMicros}µs"
            );
        ReplyLines(caller, info, "bench", lines);
    }

    [ConsoleCommand("css_pmreplay_status", "DEBUG: dump active replay / sampler state.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandPmReplayStatus(CCSPlayerController? caller, CommandInfo info)
    {
        var lines = new List<string>();
        if (_activeReplay is null)
            lines.Add("no active replay");
        else
            lines.Add(
                $"active replay: eventId={_activeReplay.EventId} members={_activeReplay.MemberCount} frame={_activeReplay.CurrentFrame}/{_activeReplay.TotalFrames}"
            );

        var stats = _perf.Snapshot(reset: false);
        foreach (var s in stats)
            lines.Add(
                $"  perf {s.OpName} n={s.Count} p50={s.P50Micros}µs p95={s.P95Micros}µs p99={s.P99Micros}µs max={s.MaxMicros}µs dropped={s.DroppedSamples}"
            );

        ReplyLines(caller, info, "status", lines);
    }

    // ===== helpers =====

    // Subtle highlight for victim/killer names embedded in chat listings
    // (deaths/events). Default chat color is white; LightYellow + LightRed
    // pop without competing with team colors.
    private static string ColorName(string name) =>
        $"{ChatColors.LightYellow}{name}{ChatColors.Default}";

    private static string ColorKillerName(string name) =>
        $"{ChatColors.LightRed}{name}{ChatColors.Default}";

    // Debounce-style chain tracker. Each death push extends a timer to
    // gap+0.5s; the chain closes when the timer fires without another push.
    // On close, if enough *T* deaths happened in the chain, alert staff —
    // CT deaths are typically the freekiller self-slaying, not victims, so
    // they shouldn't count toward the threshold. We still extend the timer
    // on CT pushes so a CT death between two T deaths doesn't close the
    // chain early; we just don't anchor on it. We re-query the stack at fire
    // time so an admin who already consumed the event with !sresevent
    // doesn't get a stale alert.
    private void TrackChainAndScheduleAlert(DeathEntry pushed)
    {
        var threshold = CvEventAlertMinDeaths.Value;
        if (threshold <= 0) return;

        if (pushed.VictimTeam == CsTeam.Terrorist)
        {
            _chainCount++;
            _chainAnchorId = pushed.Id;
        }
        else if (_chainAnchorId is null)
        {
            // CT-only chain so far — don't bother with a timer. If a T dies
            // later, that push will start the chain.
            return;
        }

        var gap = CvGroupGap.Value;
        _chainTimer?.Kill();
        _chainTimer = AddTimer(
            gap + 0.5f,
            FireChainAlertIfBigEnough,
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE
        );
    }

    private void FireChainAlertIfBigEnough()
    {
        var anchorId = _chainAnchorId;
        var count = _chainCount;
        ResetChainState();
        if (anchorId is not int id) return;

        var threshold = CvEventAlertMinDeaths.Value;
        if (threshold <= 0 || count < threshold) return;

        // Re-verify against the stack: the chain may have expanded across CT
        // deaths (so group.Count > T-count), and an admin may have consumed
        // some of it via !sresevent before the debounce fired. Count the
        // T-team members of whatever's still there.
        var group = _stack.FindGroupContainingId(id, CvGroupGap.Value);
        var tCount = 0;
        foreach (var e in group)
            if (e.VictimTeam == CsTeam.Terrorist) tCount++;
        if (tCount < threshold) return;

        var cmd = $"{ChatColors.LightYellow}!replayevent {id}{ChatColors.Red}";
        var alert = Localizer["pm.event.alert", tCount, cmd];
        var alertLine = $"{ChatPrefixColored} {ChatColors.Red}{alert}{ChatColors.Default}";
        var notified = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is null || !p.IsValid || p.IsBot) continue;
            if (!AdminManager.PlayerHasPermissions(p, "@css/generic")) continue;
            p.PrintToChat(alertLine);
            notified++;
        }
        Logger.LogInformation(
            "Postmortem: chain_alert eventId={Id} tCount={T} groupSize={Size} notifiedAdmins={N}",
            id,
            tCount,
            group.Count,
            notified
        );
    }

    private void ResetChainState()
    {
        _chainTimer?.Kill();
        _chainTimer = null;
        _chainAnchorId = null;
        _chainCount = 0;
    }

    private void MaybeCancelReplayForEntries(IReadOnlyList<DeathEntry> entries)
    {
        if (_activeReplay is null)
            return;
        // Consume-wins: cancel if any consumed entry belonged to the active
        // replay's members.
        foreach (var e in entries)
        {
            // Cheap identity check — references must match (entries came from
            // the stack, replay also holds references from the stack).
            // Safer than slot-compare since slot + time can repeat across rounds.
            // We don't have the original reference list of the replay exposed;
            // stop unconditionally if ANY entry was consumed during active play.
            StopActiveReplay(reason: "consumed");
            return;
        }
    }

    private static double MaxWindowSeconds(IReadOnlyList<DeathEntry> group)
    {
        var max = 0.0;
        foreach (var e in group)
        {
            var frames = e.MovementHistory;
            if (frames is null || frames.Length == 0)
                continue;
            var first = frames[0].TimeSinceRoundStart;
            var last = frames[^1].TimeSinceRoundStart;
            if (last - first > max)
                max = last - first;
        }
        return max;
    }

    private string FormatAgo(TimeSpan span)
    {
        var s = span.TotalSeconds;
        if (s < 5)
            return Localizer["pm.ago.just_now"];
        if (s < 60)
            return Localizer["pm.ago.seconds", (int)s];
        if (s < 3600)
            return Localizer["pm.ago.minutes", (int)(s / 60)];
        return Localizer["pm.ago.hours", (int)(s / 3600)];
    }

    // Single source of truth for the chat-line tag. Coloured form goes to
    // chat, plain form goes to console so log files stay readable.
    public string ChatPrefixColored =>
        $" {ChatColors.Green}[{CvChatPrefix.Value}]{ChatColors.Default}";
    public string ChatPrefixPlain => $"[{CvChatPrefix.Value}]";

    private void ReplyLines(
        CCSPlayerController? target,
        CommandInfo info,
        string kind,
        IEnumerable<string> lines
    )
    {
        // Route once: chat when called from a player, console when called from
        // RCON/server. `info.ReplyToCommand` for a chat-invoked command would
        // also print to the player's chat, doubling every line — so we pick
        // exactly one based on whether the caller is a player.
        foreach (var line in lines)
        {
            if (target is not null && target.IsValid)
                target.PrintToChat($"{ChatPrefixColored} {line}");
            else
                info.ReplyToCommand($"{ChatPrefixPlain} {line}");
        }
        _ = kind;
    }

    private void Reply(CCSPlayerController? target, CommandInfo info, char color, string msg)
    {
        // Single-line counterpart to ReplyLines — see the doc there for why
        // we don't call both PrintToChat and ReplyToCommand.
        if (target is not null && target.IsValid)
            target.PrintToChat($"{ChatPrefixColored} {color}{msg}{ChatColors.Default}");
        else
            info.ReplyToCommand($"{ChatPrefixPlain} {msg}");
    }
}
