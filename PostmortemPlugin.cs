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

// !pmres [N]              — respawn last N individual deaths (no grouping).
// !pmdeaths               — list recent individual deaths with #IDs.
// !pmreplay [id|name]     — play back individual death #id / newest name match / newest.
// !pmreplaystop           — cancel active replay.
// !pmevents               — list death-events (grouped for mass-respawn flow).
// !pmresevent <id>        — respawn everyone in event #id.
// !pmstats                — storage footprint + sampler counters.
// !pmrecording [on/off/toggle] — friendly toggle for pm_replay_enabled.
// !pmstack                — debug, @css/root.
// !pm_killbot <name>      — debug, @css/root.
// css_pmperfbench         — debug, @css/root.
// css_pmreplay_status     — debug, @css/root.
public partial class PostmortemPlugin : BasePlugin
{
    public override string ModuleName => "Postmortem";
    public override string ModuleVersion => "0.3.0";
    public override string ModuleAuthor => "menn";
    public override string ModuleDescription => "Admin tool: respawn + replay the last N deaths.";

    public readonly FakeConVar<float> CvGroupGap =
        new("pm_group_gap_seconds",
            "Chain-link gap for !pmevents / !pmresevent / !pmreplay grouping (0 = no grouping).",
            1.5f, ConVarFlags.FCVAR_NONE, new RangeValidator<float>(0.0f, 60.0f));

    public readonly FakeConVar<bool> CvReplayEnabled =
        new("pm_replay_enabled",
            "Master kill-switch for movement sampling + replay. Off = no sampling, no replay.",
            true);

    public readonly FakeConVar<float> CvReplayWindow =
        new("pm_replay_window_seconds",
            "Rolling movement window per player (seconds).",
            10.0f, ConVarFlags.FCVAR_NONE, new RangeValidator<float>(1.0f, 60.0f));

    public readonly FakeConVar<int> CvSampleTicksMin =
        new("pm_replay_sample_ticks_min",
            "Ticks between samples when <=10 players alive (lower = higher Hz).",
            6, ConVarFlags.FCVAR_NONE, new RangeValidator<int>(1, 32));

    public readonly FakeConVar<int> CvSampleTicksMax =
        new("pm_replay_sample_ticks_max",
            "Ticks between samples when >40 players alive (lower = higher Hz).",
            10, ConVarFlags.FCVAR_NONE, new RangeValidator<int>(1, 32));

    public readonly FakeConVar<int> CvMaxDeathsStored =
        new("pm_max_deaths_stored",
            "Safety cap on DeathStack depth. FIFO eviction when exceeded.",
            100, ConVarFlags.FCVAR_NONE, new RangeValidator<int>(32, 2000));

    public readonly FakeConVar<string> CvChatPrefix =
        new("pm_chat_prefix",
            "Chat-line tag (wrapped in [ ] on output). e.g. 'pm' → [pm].",
            "pm");

    public readonly FakeConVar<float> CvReplayLinger =
        new("pm_replay_linger_seconds",
            "Seconds to keep replay entities on screen after the last frame plays.",
            5.0f, ConVarFlags.FCVAR_NONE, new RangeValidator<float>(0.0f, 30.0f));

    private readonly DeathStack _stack = new();
    private readonly StringIntern _intern = new();
    private readonly PerfTracker _perf = new();
    private PlayerCache _cache = default!;
    private MovementSampler _sampler = default!;
    private EventRecorder _recorder = default!;
    private MovementReplay? _activeReplay;
    private float _roundStartRealtime;

    public override void Load(bool hotReload)
    {
        _cache = new PlayerCache(Logger);
        _cache.Start(this, hotReload);

        _sampler = new MovementSampler(
            this, _cache, _intern, _perf, Logger,
            enabled: () => CvReplayEnabled.Value,
            windowSeconds: () => CvReplayWindow.Value,
            sampleTicksMin: () => CvSampleTicksMin.Value,
            sampleTicksMax: () => CvSampleTicksMax.Value);
        _sampler.Start();
        _sampler.AfterTick = OnSamplerAfterTick;

        _recorder = new EventRecorder(this, _sampler, _intern, () => CvReplayEnabled.Value);
        _recorder.Start();

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _roundStartRealtime = Server.CurrentTime;
            _stack.Clear();
            StopActiveReplay(reason: "round_start");
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            var c = @event.Userid;
            if (c is not null && c.IsValid) _stack.Remove(c.Slot);
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _stack.Clear();
            StopActiveReplay(reason: "map_start");
        });
    }

    public override void Unload(bool hotReload)
    {
        StopActiveReplay(reason: "unload");
        _sampler.AfterTick = null;
        _sampler.Stop();
        _stack.Clear();
        _intern.Clear();
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim is null || !victim.IsValid) return HookResult.Continue;

        var victimName = victim.PlayerName ?? $"slot {victim.Slot}";

        MovementFrame[]? frames = null;
        List<ReplayEvent>? events = null;
        KillerSnapshot? killerSnap = null;

        if (CvReplayEnabled.Value)
        {
            _perf.Measure("replay.death_snapshot", () =>
            {
                var snap = _sampler.SnapshotForDeath(victim.Slot);
                frames = snap.Frames.Length == 0 ? null : snap.Frames;
                events = snap.Events;
                killerSnap = TryBuildKillerSnapshot(@event);
            });
        }

        // Captured unconditionally — respawn-at-death is independent of the
        // movement sampler's on/off state.
        Vector? deathPos = null;
        QAngle? deathAngles = null;
        var victimPawn = victim.PlayerPawn?.Value;
        if (victimPawn is not null && victimPawn.IsValid)
        {
            var origin = victimPawn.AbsOrigin;
            var eyes = victimPawn.EyeAngles;
            if (origin is not null) deathPos = new Vector(origin.X, origin.Y, origin.Z);
            if (eyes is not null) deathAngles = new QAngle(eyes.X, eyes.Y, eyes.Z);
        }

        var entry = new DeathEntry(
            Slot: victim.Slot,
            VictimName: victimName,
            At: DateTime.UtcNow,
            MovementHistory: frames,
            Events: events,
            KillerAt: killerSnap,
            DeathPosition: deathPos,
            DeathAngles: deathAngles);
        _stack.Push(entry, CvMaxDeathsStored.Value);

        if (_stack.Evictions > 0 && _stack.Count == CvMaxDeathsStored.Value)
        {
            // One-line warning per eviction — if this spams, raise the cap.
            Logger.LogWarning(
                "Postmortem: stack eviction — oldest DeathEntry dropped (cap={Cap} reached, evictions={E}). " +
                "Raise pm_max_deaths_stored or consume with !pmres/!pmresevent more aggressively.",
                CvMaxDeathsStored.Value, _stack.Evictions);
        }

        Logger.LogInformation(
            "Postmortem: death_push slot={Slot} name={Name} frames={Frames} events={Events} killer={Killer} stackCount={Count}",
            victim.Slot, victimName,
            frames?.Length ?? 0,
            events?.Count ?? 0,
            killerSnap?.KillerName ?? "-",
            _stack.Count);
        return HookResult.Continue;
    }

    private KillerSnapshot? TryBuildKillerSnapshot(EventPlayerDeath @event)
    {
        var attacker = @event.Attacker;
        if (attacker is null || !attacker.IsValid) return null;
        var pawn = attacker.PlayerPawn?.Value;
        if (pawn is null) return null;
        var pos = pawn.AbsOrigin ?? Vector.Zero;
        var rot = pawn.AbsRotation ?? QAngle.Zero;
        var eye = pawn.EyeAngles ?? QAngle.Zero;
        string? modelName = null;
        try
        {
            modelName = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
        }
        catch { /* best-effort */ }
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
            KillerJbRoleFlags: 0);
    }

    private void OnSamplerAfterTick()
    {
        if (_activeReplay is null) return;
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
            StopActiveReplay(reason: "playback_end");
        }
    }

    private void StopActiveReplay(string reason)
    {
        if (_activeReplay is null) return;
        Logger.LogInformation("Postmortem: replay_stop reason={Reason} eventId={Id}", reason, _activeReplay.EventId);
        _activeReplay.Stop();
        _activeReplay = null;
    }

    // ===== Commands =====

    [ConsoleCommand("css_pmres", "Respawn the last N individual dead players (default 1).")]
    [CommandHelper(minArgs: 0, usage: "[count] [spawn|death]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmRes(CCSPlayerController? caller, CommandInfo info)
    {
        var count = 1;
        if (info.ArgCount >= 2)
        {
            if (!int.TryParse(info.GetArg(1), out count) || count <= 0)
            {
                Reply(caller, info, ChatColors.Red, Localizer["pm.res.bad_count"]);
                return;
            }
        }
        if (!TryParseRespawnWhere(info, 2, out var atDeath, caller)) return;

        var popped = _stack.PopLastN(count);
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
            if (c is null || !c.IsValid) { skipped++; continue; }
            if (c.PawnIsAlive) { skipped++; continue; }
            if (c.Team != CsTeam.Terrorist && c.Team != CsTeam.CounterTerrorist) { skipped++; continue; }
            RespawnAt(c, e, atDeath);
            names.Add(c.PlayerName ?? $"slot {e.Slot}");
        }

        // If we consumed entries that belong to the active replay, cancel it
        // (consume-wins per plan).
        MaybeCancelReplayForEntries(popped);

        if (names.Count == 0)
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.res.partial", popped.Count]);
        else
            Reply(caller, info, ChatColors.Green,
                Localizer["pm.res.ok", names.Count, popped.Count, string.Join(", ", names)]);
        Logger.LogInformation(
            "Postmortem: pmres popped={Popped} respawned={Respawned} skipped={Skipped} requested={Req} atDeath={At}",
            popped.Count, names.Count, skipped, count, atDeath);
    }

    [ConsoleCommand("css_pmevents", "List recent death-events with #IDs.")]
    [ConsoleCommand("css_pmev", "Alias of !pmevents.")]
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
            Localizer["pm.events.header", groups.Count, gap.ToString("F1")]
        };
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            // newest → oldest within the group; names in chronological order.
            var victimNames = new List<string>(g.Count);
            for (var k = g.Count - 1; k >= 0; k--) victimNames.Add(g[k].VictimName);
            var newestAt = g[0].At;
            var oldestAt = g[^1].At;
            var ago = FormatAgo(now - newestAt);
            var victimsLabel = string.Join(", ", victimNames);
            if (g.Count == 1)
                lines.Add(Localizer["pm.events.row_one", i + 1, ago, victimsLabel]);
            else
            {
                var duration = (newestAt - oldestAt).TotalSeconds.ToString("F1");
                lines.Add(Localizer["pm.events.row_many", i + 1, ago, g.Count, duration, victimsLabel]);
            }
        }
        ReplyLines(caller, info, "events", lines);
    }

    [ConsoleCommand("css_pmresevent", "Respawn everyone in event #id.")]
    [ConsoleCommand("css_pmre", "Alias of !pmresevent.")]
    [CommandHelper(minArgs: 1, usage: "<event_id> [spawn|death]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmResEvent(CCSPlayerController? caller, CommandInfo info)
    {
        if (!int.TryParse(info.GetArg(1), out var id) || id <= 0)
        {
            Reply(caller, info, ChatColors.Red, Localizer["pm.event.bad_id"]);
            return;
        }
        if (!TryParseRespawnWhere(info, 2, out var atDeath, caller)) return;

        var popped = _stack.PopGroup(id - 1, CvGroupGap.Value);
        if (popped.Count == 0)
        {
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.event.none", id]);
            return;
        }

        var names = new List<string>(popped.Count);
        foreach (var e in popped)
        {
            var c = Utilities.GetPlayerFromSlot(e.Slot);
            if (c is null || !c.IsValid) continue;
            if (c.PawnIsAlive) continue;
            if (c.Team != CsTeam.Terrorist && c.Team != CsTeam.CounterTerrorist) continue;
            RespawnAt(c, e, atDeath);
            names.Add(c.PlayerName ?? $"slot {e.Slot}");
        }

        MaybeCancelReplayForEntries(popped);

        if (names.Count == 0)
            Reply(caller, info, ChatColors.Yellow,
                Localizer["pm.resevent.partial", popped.Count, id]);
        else
            Reply(caller, info, ChatColors.Green,
                Localizer["pm.resevent.ok", names.Count, popped.Count, id, string.Join(", ", names)]);
        Logger.LogInformation("Postmortem: pmresevent id={Id} popped={Popped} respawned={R} atDeath={At}",
            id, popped.Count, names.Count, atDeath);
    }

    // Parses the optional `spawn|death` trailing arg shared by !pmres and
    // !pmresevent. Returns true when the arg is absent or valid (atDeath set);
    // false + error message to caller when invalid.
    private bool TryParseRespawnWhere(CommandInfo info, int argIndex, out bool atDeath, CCSPlayerController? caller)
    {
        atDeath = false;
        if (info.ArgCount <= argIndex) return true;
        var arg = info.GetArg(argIndex).ToLowerInvariant();
        switch (arg)
        {
            case "spawn": case "team":   atDeath = false; return true;
            case "death": case "here": case "at": atDeath = true; return true;
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
        if (!atDeath || entry.DeathPosition is null) return;
        var pos = entry.DeathPosition;
        var ang = entry.DeathAngles ?? QAngle.Zero;
        Server.NextFrame(() =>
        {
            if (!c.IsValid) return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn is null || !pawn.IsValid) return;
            pawn.Teleport(pos, ang, Vector.Zero);
        });
    }

    [ConsoleCommand("css_pmdeaths", "List recent individual deaths with #IDs.")]
    [ConsoleCommand("css_pmd", "Alias of !pmdeaths.")]
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
            var id = i + 1;
            var ago = FormatAgo(now - e.At);
            var killer = e.KillerAt;
            string line;
            if (killer is null)
                line = Localizer["pm.deaths.row", id, ago, e.VictimName];
            else if (killer.KillerSlot == e.Slot)
                line = Localizer["pm.deaths.row_suicide", id, ago, e.VictimName];
            else
                line = Localizer["pm.deaths.row_kill", id, ago, e.VictimName, killer.KillerName];
            lines.Add(line);
        }
        ReplyLines(caller, info, "deaths", lines);
    }

    [ConsoleCommand("css_pmreplay", "Play back individual death #id or the newest death matching <name>.")]
    [ConsoleCommand("css_pmr", "Alias of !pmreplay.")]
    [CommandHelper(minArgs: 0, usage: "[death_id|name]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmReplay(CCSPlayerController? caller, CommandInfo info)
    {
        var snap = _stack.SnapshotNewestFirst();

        // Resolve the individual death to play: by explicit 1-based id
        // (newest-first), by name substring (newest match wins), or default
        // to the newest death.
        int id;
        if (info.ArgCount < 2)
        {
            id = 1;
        }
        else if (int.TryParse(info.GetArg(1), out id) && id > 0)
        {
            // explicit numeric death id — fall through
        }
        else
        {
            var needle = info.GetArg(1);
            if (!TryFindDeathByName(snap, needle, out id))
            {
                Reply(caller, info, ChatColors.Yellow, Localizer["pm.replay.no_name_match", needle]);
                return;
            }
        }

        if (id - 1 >= snap.Count)
        {
            Reply(caller, info, ChatColors.Yellow, Localizer["pm.replay.no_id", id]);
            return;
        }

        var entry = snap[id - 1];
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
        _activeReplay = new MovementReplay(id, members, Logger, FormatReplayKillLine,
            lingerSeconds: () => CvReplayLinger.Value);
        var windowSec = MaxWindowSeconds(members);
        Reply(caller, info, ChatColors.Green,
            Localizer["pm.replay.started", id, entry.VictimName, windowSec.ToString("F1")]);
        Logger.LogInformation("Postmortem: replay_start deathId={Id} victim={V}",
            id, entry.VictimName);
    }

    // Newest-first match of `needle` against victim names. Returns the 1-based
    // death id of the first entry containing a match, so the caller can re-use
    // the existing id code path.
    private static bool TryFindDeathByName(
        IReadOnlyList<DeathEntry> snapNewestFirst, string needle, out int deathId)
    {
        deathId = 0;
        if (string.IsNullOrWhiteSpace(needle)) return false;
        for (var i = 0; i < snapNewestFirst.Count; i++)
        {
            if (snapNewestFirst[i].VictimName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                deathId = i + 1;
                return true;
            }
        }
        return false;
    }

    [ConsoleCommand("css_pmreplaystop", "Cancel the active replay.")]
    [ConsoleCommand("css_pmrs", "Alias of !pmreplaystop.")]
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
    // line so replay announcements match the caller's language.
    private string FormatReplayKillLine(string killer, string victim, string weapon)
        => $"{ChatPrefixColored} {Localizer["pm.replay.killshot", killer, victim, weapon]}";

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

        const int frameBytes = 72;  // rough per-reference size incl. two strings + two Vectors + QAngles
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
    [CommandHelper(minArgs: 0, usage: "[on|off|toggle]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/generic")]
    public void OnCommandPmRecording(CCSPlayerController? caller, CommandInfo info)
    {
        var arg = info.ArgCount >= 2 ? info.GetArg(1).ToLowerInvariant() : "toggle";
        bool newState = CvReplayEnabled.Value;
        switch (arg)
        {
            case "on":     newState = true; break;
            case "off":    newState = false; break;
            case "toggle": newState = !CvReplayEnabled.Value; break;
            default:
                Reply(caller, info, ChatColors.Red, Localizer["pm.recording.bad_arg"]);
                return;
        }
        CvReplayEnabled.Value = newState;
        Reply(caller, info, newState ? ChatColors.Green : ChatColors.Yellow,
            Localizer[newState ? "pm.recording.on" : "pm.recording.off"]);
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
            if (anchor is null) anchor = e.At;
            else if ((anchor.Value - e.At).TotalSeconds > gap)
            {
                lines.Add("  ---");
                groupIdx++;
                anchor = e.At;
            }
            else anchor = e.At;

            var c = Utilities.GetPlayerFromSlot(e.Slot);
            var name = c?.PlayerName ?? e.VictimName;
            var alive = c is not null && c.IsValid && c.PawnIsAlive ? "alive" : "dead";
            var age = (now - e.At).TotalSeconds;
            var frames = e.MovementHistory?.Length ?? 0;
            var events = e.Events?.Count ?? 0;
            var killer = e.KillerAt?.KillerName ?? "-";
            var pos = e.DeathPosition is { } p ? $"({p.X:F0},{p.Y:F0},{p.Z:F0})" : "-";
            lines.Add($"  g{groupIdx} [{i}] slot={e.Slot} name={name} age={age:F1}s ({alive}) frames={frames} events={events} killer={killer} deathpos={pos}");
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
            if (c is null || !c.IsValid || !c.IsBot) continue;
            if (!string.Equals(c.PlayerName, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!c.PawnIsAlive) { info.ReplyToCommand($"{ChatPrefixPlain} {name} already dead"); return; }
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
        foreach (var entry in _cache.AliveCombatants()) players.Add(entry.Controller);

        var iterations = 10_000;
        var sw = Stopwatch.StartNew();
        var samplesTaken = 0;
        for (var i = 0; i < iterations; i++)
        {
            if (players.Count == 0)
            {
                // No-op workload — still exercises PerfTracker plumbing.
                _perf.Measure("replay.bench.nop", () => { samplesTaken++; });
                continue;
            }
            var p = players[i % players.Count];
            _perf.Measure("replay.bench.one_player", () =>
            {
                var pawn = p.PlayerPawn?.Value;
                if (pawn is null) return;
                var _loc = pawn.AbsOrigin ?? Vector.Zero;
                var _eye = pawn.EyeAngles ?? QAngle.Zero;
                var _rot = pawn.AbsRotation ?? QAngle.Zero;
                var _vel = pawn.AbsVelocity ?? Vector.Zero;
                var _dn = pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName;
                samplesTaken++;
            });
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
            lines.Add($"  op={stats.OpName} n={stats.Count} p50={stats.P50Micros}µs p95={stats.P95Micros}µs p99={stats.P99Micros}µs max={stats.MaxMicros}µs");
        ReplyLines(caller, info, "bench", lines);
    }

    [ConsoleCommand("css_pmreplay_status", "DEBUG: dump active replay / sampler state.")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [RequiresPermissions("@css/root")]
    public void OnCommandPmReplayStatus(CCSPlayerController? caller, CommandInfo info)
    {
        var lines = new List<string>();
        if (_activeReplay is null) lines.Add("no active replay");
        else lines.Add($"active replay: eventId={_activeReplay.EventId} members={_activeReplay.MemberCount} frame={_activeReplay.CurrentFrame}/{_activeReplay.TotalFrames}");

        var stats = _perf.Snapshot(reset: false);
        foreach (var s in stats)
            lines.Add($"  perf {s.OpName} n={s.Count} p50={s.P50Micros}µs p95={s.P95Micros}µs p99={s.P99Micros}µs max={s.MaxMicros}µs dropped={s.DroppedSamples}");

        ReplyLines(caller, info, "status", lines);
    }

    // ===== helpers =====

    private void MaybeCancelReplayForEntries(IReadOnlyList<DeathEntry> entries)
    {
        if (_activeReplay is null) return;
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
            if (frames is null || frames.Length == 0) continue;
            var first = frames[0].TimeSinceRoundStart;
            var last = frames[^1].TimeSinceRoundStart;
            if (last - first > max) max = last - first;
        }
        return max;
    }

    private string FormatAgo(TimeSpan span)
    {
        var s = span.TotalSeconds;
        if (s < 5) return Localizer["pm.ago.just_now"];
        if (s < 60) return Localizer["pm.ago.seconds", (int)s];
        if (s < 3600) return Localizer["pm.ago.minutes", (int)(s / 60)];
        return Localizer["pm.ago.hours", (int)(s / 3600)];
    }

    // Single source of truth for the chat-line tag. Coloured form goes to
    // chat, plain form goes to console so log files stay readable.
    public string ChatPrefixColored => $" {ChatColors.Green}[{CvChatPrefix.Value}]{ChatColors.Default}";
    public string ChatPrefixPlain => $"[{CvChatPrefix.Value}]";

    private void ReplyLines(CCSPlayerController? target, CommandInfo info, string kind, IEnumerable<string> lines)
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
