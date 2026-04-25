using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Menn.Utils;
using Microsoft.Extensions.Logging;

namespace Postmortem.Replay;

// Timer-driven per-player movement sampler.
//
// Adaptive-rate table (ticks between samples; yappers-style):
//   alive <= 10  -> every 6 ticks (~10.7 Hz at 64 tick)
//   alive <= 20  -> 7
//   alive <= 30  -> 8
//   alive <= 40  -> 9
//   alive >  40  -> 10
//
// Interval is recomputed at EventRoundStart; the timer recreates itself if
// the interval changed. Cheap — one timer total, runs on the game thread.
//
// Per-slot live ring buffer is allocated lazily on the first sample for that
// slot. Size = ceil(window_seconds / (tick_interval * sample_ticks)) + 8
// headroom. Because the rate is computed once per round, the buffer may be
// slightly over- or under-sized if the alive count changes mid-round — we
// size for the *low* rate (fewer samples) so we never overshoot.
//
// Hot-path contract:
//   - Zero per-sample heap alloc (reused MovementFrame slots in the ring).
//   - No Utilities.GetPlayers() — use PlayerCache.AliveCombatants().
//   - All work wrapped in PerfTracker.Measure for observability.
public sealed class MovementSampler
{
    private readonly BasePlugin _plugin;
    private readonly PlayerCache _cache;
    private readonly StringIntern _intern;
    private readonly PerfTracker _perf;
    private readonly ILogger _logger;

    private readonly Func<bool> _enabled;
    private readonly Func<float> _windowSeconds;
    private readonly Func<int> _sampleTicksMin;
    private readonly Func<int> _sampleTicksMax;

    // Per-slot state.
    private const int MaxSlots = 65;
    private readonly RingBuffer?[] _buffers = new RingBuffer?[MaxSlots];
    private readonly List<ReplayEvent>?[] _eventLogs = new List<ReplayEvent>?[MaxSlots];

    private CounterStrikeSharp.API.Modules.Timers.Timer? _timer;
    private int _currentIntervalTicks;
    private float _roundStartRealtime;

    public long TickCount { get; private set; }
    public long SamplesTaken { get; private set; }

    // Plugin hooks the current replay here so playback advances on the same
    // timer as the sampler (no second timer). Null when no replay active.
    public Action? AfterTick { get; set; }

    public MovementSampler(
        BasePlugin plugin,
        PlayerCache cache,
        StringIntern intern,
        PerfTracker perf,
        ILogger logger,
        Func<bool> enabled,
        Func<float> windowSeconds,
        Func<int> sampleTicksMin,
        Func<int> sampleTicksMax)
    {
        _plugin = plugin;
        _cache = cache;
        _intern = intern;
        _perf = perf;
        _logger = logger;
        _enabled = enabled;
        _windowSeconds = windowSeconds;
        _sampleTicksMin = sampleTicksMin;
        _sampleTicksMax = sampleTicksMax;
    }

    public void Start()
    {
        _plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _roundStartRealtime = Server.CurrentTime;
            OnRoundStart();
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventPlayerSpawn>((@event, _) =>
        {
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;
            // Fresh life → fresh window. Reuse the ring buffer slot if one
            // already exists; no allocation on respawn.
            _buffers[c.Slot]?.Clear();
            _eventLogs[c.Slot]?.Clear();
            return HookResult.Continue;
        });

        _plugin.RegisterEventHandler<EventPlayerDisconnect>((@event, _) =>
        {
            var c = @event.Userid;
            if (c is null || !c.IsValid) return HookResult.Continue;
            _buffers[c.Slot] = null;
            _eventLogs[c.Slot] = null;
            return HookResult.Continue;
        });

        _plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            for (var i = 0; i < MaxSlots; i++)
            {
                _buffers[i] = null;
                _eventLogs[i] = null;
            }
            _intern.Clear();
            _roundStartRealtime = Server.CurrentTime;
            ResetTimer();
        });

        // First tick uses the min rate until the first EventRoundStart tunes it.
        ResetTimer();
    }

    public void Stop()
    {
        _timer?.Kill();
        _timer = null;
        for (var i = 0; i < MaxSlots; i++)
        {
            _buffers[i] = null;
            _eventLogs[i] = null;
        }
    }

    // Called from the plugin when pm_replay_window_seconds changes mid-round —
    // resize each live buffer to the new capacity. Buffers whose capacity
    // already matches are left untouched (no data loss); mismatched ones are
    // dropped so the next sample reallocates at the right size. This is
    // exactly what OnRoundStart does already, exposed as a public hook.
    public void RebuildBuffers()
    {
        var targetCapacity = ComputeRingCapacity();
        for (var i = 0; i < MaxSlots; i++)
        {
            var buf = _buffers[i];
            if (buf is null) continue;
            if (buf.Capacity != targetCapacity)
                _buffers[i] = null;
        }
    }

    // Called from the plugin when pm_replay_sample_ticks_min/max changes —
    // recompute the interval and recreate the timer if it differs. Cheap;
    // ResetTimer no-ops when the chosen interval is unchanged.
    public void RecomputeInterval() => ResetTimer();

    private void OnRoundStart()
    {
        // Clear any stale buffers from the last round — fresh windows per life.
        // Buffers whose capacity doesn't match the current target (admin
        // changed pm_replay_window_seconds mid-game) are dropped entirely so
        // the next SampleOne reallocates them at the new size. Without this,
        // window-length changes only took effect on map change.
        var targetCapacity = ComputeRingCapacity();
        for (var i = 0; i < MaxSlots; i++)
        {
            var buf = _buffers[i];
            if (buf is null) continue;
            if (buf.Capacity != targetCapacity)
                _buffers[i] = null;
            else
                buf.Clear();
            _eventLogs[i]?.Clear();
        }
        ResetTimer();
    }

    private void ResetTimer()
    {
        var interval = ChooseIntervalTicks();
        if (_timer is not null && interval == _currentIntervalTicks) return;
        _timer?.Kill();
        _currentIntervalTicks = interval;
        _timer = _plugin.AddTimer(
            Server.TickInterval * interval,
            OnTick,
            TimerFlags.STOP_ON_MAPCHANGE | TimerFlags.REPEAT);
    }

    private int ChooseIntervalTicks()
    {
        var aliveCount = 0;
        foreach (var _ in _cache.AliveCombatants()) aliveCount++;
        var min = Math.Clamp(_sampleTicksMin(), 1, 32);
        var max = Math.Clamp(_sampleTicksMax(), min, 32);
        return aliveCount switch
        {
            <= 10 => min,
            <= 20 => Math.Min(min + 1, max),
            <= 30 => Math.Min(min + 2, max),
            <= 40 => Math.Min(min + 3, max),
            _     => max,
        };
    }

    private void OnTick()
    {
        if (_enabled())
        {
            _perf.Measure("replay.sampler_tick", () =>
            {
                TickCount++;
                // Iterate every cached slot and check alive/team via the live
                // controller. The cache's IsAlive/Team flags can lag the
                // engine state on team-select-screen maps (jb_clouds_d) or
                // after hot reload — missing EventPlayerSpawn/Team fires mean
                // a player stays IsAlive=false in the cache, the sampler
                // skips them, and their death snapshot ends up empty.
                // Three native reads per slot per tick — still cheap.
                foreach (var entry in _cache.All())
                {
                    var c = entry.Controller;
                    if (c is null || !c.IsValid) continue;
                    if (!c.PawnIsAlive) continue;
                    var team = c.Team;
                    if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist) continue;
                    _perf.Measure("replay.sample_one_player", () => SampleOne(entry));
                }
            });
        }
        // Drive replay playback off the same timer regardless of sampling
        // state — the replay reads already-captured data.
        AfterTick?.Invoke();
    }

    private void SampleOne(PlayerCache.Entry entry)
    {
        var pawn = entry.Controller.PlayerPawn?.Value;
        if (pawn is null) return;

        var slot = entry.Slot;
        var buf = _buffers[slot];
        if (buf is null)
        {
            var capacity = ComputeRingCapacity();
            buf = new RingBuffer(capacity);
            _buffers[slot] = buf;
            _eventLogs[slot] = new List<ReplayEvent>(16);
        }

        var frame = buf.NextSlot();
        // Vector/QAngle in CSSharp are NativeObject wrappers around a live
        // pointer into engine memory (see Utils/Vector.cs — X/Y/Z return
        // `ref float` via GetElementRef). Storing `pawn.AbsOrigin` by reference
        // means every subsequent read tracks the pawn's current position, so a
        // whole buffer of "frames" would all resolve to wherever the pawn is
        // *now* — and after the pawn dies, wherever it came to rest. Copy the
        // primitives into the frame's own managed Vector/QAngle instances
        // (initialized in MovementFrame.cs) so the snapshot stays frozen.
        CopyVec(pawn.AbsOrigin, frame.Location);
        CopyAng(pawn.EyeAngles, frame.ViewAngles);
        CopyAng(pawn.AbsRotation, frame.PlayerRotation);
        CopyVec(pawn.AbsVelocity, frame.Velocity);
        frame.ActiveWeaponDesignerName = _intern.Intern(
            pawn.WeaponServices?.ActiveWeapon?.Value?.DesignerName);
        frame.ModelRenderColor = Color.FromArgb(pawn.Render.A, pawn.Render.R, pawn.Render.G, pawn.Render.B);
        frame.ModelName = _intern.Intern(TryGetModelName(pawn));
        frame.ShotDirection = null;
        frame.JbRoleFlags = 0;
        frame.TimeSinceRoundStart = Server.CurrentTime - _roundStartRealtime;

        // Crouch flag — PlayerFlags.FL_DUCKING = 2 (bit 1).
        var pawnBase = entry.Controller.Pawn?.Value;
        frame.IsCrouching = pawnBase is not null
            && ((PlayerFlags)pawnBase.Flags & PlayerFlags.FL_DUCKING) != 0;

        SamplesTaken++;
    }

    private static void CopyVec(Vector? src, Vector dst)
    {
        if (src is null) { dst.X = 0; dst.Y = 0; dst.Z = 0; return; }
        dst.X = src.X; dst.Y = src.Y; dst.Z = src.Z;
    }

    private static void CopyAng(QAngle? src, QAngle dst)
    {
        if (src is null) { dst.X = 0; dst.Y = 0; dst.Z = 0; return; }
        dst.X = src.X; dst.Y = src.Y; dst.Z = src.Z;
    }

    private static string? TryGetModelName(CCSPlayerPawn pawn)
    {
        try
        {
            return pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
        }
        catch
        {
            return null;
        }
    }

    private int ComputeRingCapacity()
    {
        var window = Math.Clamp(_windowSeconds(), 1f, 60f);
        var tickSec = Server.TickInterval <= 0 ? 1f / 64f : Server.TickInterval;
        var ticksPerSec = 1f / (tickSec * Math.Max(_currentIntervalTicks, 1));
        var needed = (int)Math.Ceiling(window * ticksPerSec) + 8;
        return Math.Clamp(needed, 16, 2048);
    }

    // Called by EventPlayerDeath (in the plugin's handler) — snapshot + clear
    // the victim's live buffer + event log into fresh arrays for the DeathEntry.
    public (MovementFrame[] Frames, List<ReplayEvent>? Events) SnapshotForDeath(int slot)
    {
        var buf = _buffers[slot];
        var log = _eventLogs[slot];
        var frames = buf is null ? Array.Empty<MovementFrame>() : buf.SnapshotCopy();
        var copiedLog = log is null || log.Count == 0
            ? null
            : new List<ReplayEvent>(log);
        buf?.Clear();
        log?.Clear();
        return (frames, copiedLog);
    }

    // Non-destructive snapshot of a slot's live buffer — used at kill time to
    // grab the *killer's* recent frames so they can be replayed alongside the
    // victim. We don't clear: the killer is still alive and their buffer needs
    // to keep filling toward their next death.
    public MovementFrame[]? PeekFramesForSlot(int slot)
    {
        var buf = _buffers[slot];
        if (buf is null || buf.Count == 0) return null;
        return buf.SnapshotCopy();
    }

    // Append to the slot's event log, if sampling is on and the log exists.
    // Returns the last frame in the live ring so the caller can stamp shot
    // direction on it (used by the weapon-fire hook).
    public MovementFrame? TryStampShot(int slot, QAngle shotDir)
    {
        var buf = _buffers[slot];
        if (buf is null) return null;
        var last = buf.LastOrDefault();
        if (last is null) return null;
        // Copy values — not the reference — see CopyVec/CopyAng for the reason.
        last.ShotDirection ??= new QAngle();
        CopyAng(shotDir, last.ShotDirection);
        return last;
    }

    public void AppendEvent(int slot, ReplayEvent ev)
    {
        var log = _eventLogs[slot];
        log?.Add(ev);
    }

    public bool HasBufferForSlot(int slot) => _buffers[slot] is not null;
    public int CurrentIntervalTicks => _currentIntervalTicks;

    public (int LiveBuffers, int TotalFrames) LiveBufferStats()
    {
        var live = 0;
        var frames = 0;
        for (var i = 0; i < MaxSlots; i++)
        {
            if (_buffers[i] is { } b)
            {
                live++;
                frames += b.Count;
            }
        }
        return (live, frames);
    }
}
