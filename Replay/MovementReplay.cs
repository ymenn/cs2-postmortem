using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Postmortem;

namespace Postmortem.Replay;

// Playback of a death event. Spawns one prop_dynamic ghost per member at the
// member's first frame, advances all members together on a unified timeline
// (driven by the sampler's timer — no second timer), despawns each ghost at
// its own last frame. Main-ghost + green glow prop follows it, look beam
// tracks eye direction, shot beams flash red on frames with ShotDirection,
// blue kill-shot beam on the final frame from killer → victim.
//
// Single active replay enforced by the plugin (plugin holds a nullable field;
// starting a new one Stop()s the old).
public sealed class MovementReplay
{
    // Held on screen after the last frame so the kill-shot beam + killer
    // ghost are readable. Fetched each Tick so `pm_replay_linger_seconds`
    // takes effect mid-playback if the admin retunes it live.
    private readonly Func<float> _lingerSeconds;

    private readonly ILogger _logger;
    // First float arg is seconds-since-replay-start so the formatter can
    // prepend a `[+X.Xs]` timestamp — admins reading the replay chat want to
    // see when each action happened relative to playback start.
    private readonly Func<float, string, string, string, string> _formatKillLine;
    // Formatter for per-action chat lines ("T fired {weapon}", "T threw
    // {weapon}", "T swung a knife"). Called from AdvanceMember when we cross
    // an event boundary and the weapon differs from the last announced one.
    private readonly Func<float, string, string, string>? _formatShotLine;
    private readonly List<MemberChannel> _channels;
    private readonly DateTime _startedAt;
    private int _frameIndex;
    public bool IsPlaying { get; private set; } = true;
    public int EventId { get; }
    public int MemberCount => _channels.Count;
    public int VictimCount
    {
        get
        {
            var n = 0;
            foreach (var ch in _channels)
                if (!ch.IsKillerCompanion) n++;
            return n;
        }
    }
    public int CurrentFrame => _frameIndex;
    public int TotalFrames { get; }

    private sealed class MemberChannel
    {
        public DeathEntry Entry = default!;
        public MovementFrame[] Frames = Array.Empty<MovementFrame>();
        public CDynamicProp? Ghost;
        public CBaseModelEntity? GhostGlow;  // green glow (T) or red glow (CT companion)
        public CDynamicProp? KillerGhost;    // spawned at kill moment, only when no animated companion
        public CBaseModelEntity? KillerGlow; // red glow for the static CT ghost
        public CEnvBeam? LookBeam;
        public readonly List<CEnvBeam> ShotBeams = new();
        public bool KillShotDrawn;
        public bool Finished;
        public DateTime? FinishedAt;         // used for linger window
        public bool Despawned;
        // Event cursor + de-dupe state for T-action chat lines. Events are
        // timestamped by seconds-since-round-start on the same timeline as
        // MovementFrame.TimeSinceRoundStart. We walk the cursor forward each
        // tick and emit a chat line when the T's weapon switches.
        public int NextEventIdx;
        public string? LastAnnouncedWeapon;
        // End-alignment: every channel finishes on _frameIndex = TotalFrames-1
        // (= the kill moment). Channels with shorter histories sit idle until
        // _frameIndex reaches StartFrameDelay, then start playing. Lazy spawn
        // keeps prop_dynamic ghosts off-map until their first frame plays.
        public int StartFrameDelay;
        public bool Spawned;
        // Killer-companion (CT) channel rather than a victim (T) channel. Drives
        // glow color (red), suppresses victim-tier tint, and skips the kill-shot
        // beam / static killer-ghost spawn (those belong to the victim channel).
        public bool IsKillerCompanion;
        // Set on a victim channel when a killer-companion channel was created
        // for it — FinishMember then skips the static SpawnKillerGhost (the
        // animated companion already covers that role).
        public bool HasAnimatedKiller;
    }

    public MovementReplay(
        int eventId,
        IReadOnlyList<DeathEntry> members,
        ILogger logger,
        Func<float, string, string, string, string> formatKillLine,
        Func<float> lingerSeconds,
        Func<float, string, string, string>? formatShotLine = null)
    {
        EventId = eventId;
        _logger = logger;
        _formatKillLine = formatKillLine;
        _formatShotLine = formatShotLine;
        _lingerSeconds = lingerSeconds;
        _channels = new List<MemberChannel>(members.Count * 2);
        _startedAt = DateTime.UtcNow;

        // Track which slots we've already added a killer-companion for, so a
        // multi-victim event with a single killer (rambo) doesn't double-spawn
        // the same ghost. Also avoid adding a killer-companion for someone
        // who's already in `members` as their own victim — they'd render twice.
        var memberSlots = new HashSet<int>();
        foreach (var m in members) memberSlots.Add(m.Slot);
        var killerSlotsAdded = new HashSet<int>();

        foreach (var m in members)
        {
            var vFrames = m.MovementHistory ?? Array.Empty<MovementFrame>();
            if (vFrames.Length == 0) continue;
            var victimCh = new MemberChannel
            {
                Entry = m,
                Frames = vFrames,
            };
            _channels.Add(victimCh);

            if (m.KillerMovementHistory is { Length: > 0 } kFrames
                && m.KillerAt is { } killerSnap
                && killerSnap.KillerSlot != m.Slot
                && !memberSlots.Contains(killerSnap.KillerSlot)
                && killerSlotsAdded.Add(killerSnap.KillerSlot))
            {
                _channels.Add(new MemberChannel
                {
                    Entry = m,
                    Frames = kFrames,
                    IsKillerCompanion = true,
                });
            }
        }

        // Mark every victim whose killer is rendered as an animated companion,
        // not just the first one we picked the companion off of. Without this
        // pass, multi-victim events with one shared killer leave victims[1..]
        // with HasAnimatedKiller=false, so FinishMember spawns the static
        // killer-ghost on each of them — a leftover from the pre-companion
        // rendering that shows up as a second CT prop popping in at each
        // subsequent kill tick.
        foreach (var ch in _channels)
        {
            if (ch.IsKillerCompanion) continue;
            if (ch.Entry.KillerAt is { } k && killerSlotsAdded.Contains(k.KillerSlot))
                ch.HasAnimatedKiller = true;
        }

        // Start-align: anchor on the earliest first-frame timestamp across all
        // channels, then offset each channel by how much later its own first
        // frame happened. Channels finish naturally when their own frames run
        // out — earlier-killed victims despawn earlier in the replay, later
        // victims later. Frame timestamps come from the sampler, so all
        // channels share a sample rate; we derive the interval from the
        // longest channel rather than reading the live ConVar so a mid-replay
        // retune doesn't break the math.
        var anchorTime = float.MaxValue;
        foreach (var ch in _channels)
            if (ch.Frames.Length > 0 && ch.Frames[0].TimeSinceRoundStart < anchorTime)
                anchorTime = ch.Frames[0].TimeSinceRoundStart;

        var refCh = _channels[0];
        foreach (var ch in _channels)
            if (ch.Frames.Length > refCh.Frames.Length) refCh = ch;
        var sampleInterval = refCh.Frames.Length >= 2
            ? Math.Max(0.001f,
                (refCh.Frames[^1].TimeSinceRoundStart - refCh.Frames[0].TimeSinceRoundStart)
                    / (refCh.Frames.Length - 1))
            : 0.1f;  // 10 Hz fallback for a degenerate single-frame channel

        var maxEnd = 0;
        foreach (var ch in _channels)
        {
            if (ch.Frames.Length == 0) { ch.StartFrameDelay = 0; continue; }
            var delaySec = ch.Frames[0].TimeSinceRoundStart - anchorTime;
            ch.StartFrameDelay = Math.Max(0, (int)MathF.Round(delaySec / sampleInterval));
            var end = ch.StartFrameDelay + ch.Frames.Length;
            if (end > maxEnd) maxEnd = end;
        }
        TotalFrames = maxEnd;
    }

    private static void SpawnMember(MemberChannel ch)
    {
        if (ch.Frames.Length == 0) return;
        var modelName = ch.Frames[0].ModelName
            ?? ch.Entry.KillerAt?.KillerModelName
            ?? "characters/models/tm_leet/tm_leet_varianta.vmdl";

        var ghost = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (ghost is null) return;
        try
        {
            ghost.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags = (uint)(
                ghost.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags & ~(1 << 2));
        }
        catch { /* best-effort; older CSSharp may not expose this */ }
        // Let the model's own animgraph drive the pose. Setting
        // UseAnimGraph=false forces manual SetAnimation calls — which only
        // works if the model still embeds the expected named sequences.
        // Modern CS2 player models are graph-driven
        // (animation/graphs/worldmodel/worldmodel.vnmgraph) and carry almost
        // no raw sequences, so SetAnimation just logs warnings. With the
        // graph enabled and no control parameters fed, the prop runs the
        // graph's default state (idle/stand).
        ghost.UseAnimGraph = true;
        ghost.Teleport(ch.Frames[0].Location);
        // DispatchSpawn before SetModel produces a benign "no model name"
        // warning from the engine on first frame. Reversing it breaks the
        // prop entirely (no visible entity). Left as-is.
        ghost.DispatchSpawn();
        ghost.SetModel(modelName);
        ch.Ghost = ghost;

        // Glow color: green for T (victim), red for the CT (killer companion)
        // so admins can read the engagement at a glance — same convention as
        // the static SpawnKillerGhost path.
        var glowColor = ch.IsKillerCompanion ? Color.Red : Color.Green;
        var glow = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        if (glow is not null)
        {
            glow.DispatchSpawn();
            glow.SetModel(modelName);
            glow.Spawnflags = 256u;
            glow.Glow.GlowColorOverride = glowColor;
            glow.Glow.GlowRange = 0;
            glow.Glow.GlowTeam = -1;
            glow.Glow.GlowType = 3;
            glow.Glow.GlowRangeMin = 0;
            glow.Render = Color.FromArgb(5, 255, 255, 255);
            glow.AcceptInput("FollowEntity", ghost, glow, "!activator");
            ch.GhostGlow = glow;
        }
    }

    // Spawned when the victim reaches its final frame (not at replay start),
    // so the killer appears only at the kill moment. Skip pure suicides
    // (killer slot == victim slot) but show for bot-on-bot kills (steamIds
    // both 0, but slots differ).
    private static void SpawnKillerGhost(MemberChannel ch)
    {
        var killer = ch.Entry.KillerAt;
        if (killer is null || killer.KillerSlot == ch.Entry.Slot) return;
        if (string.IsNullOrEmpty(killer.KillerModelName)) return;
        var km = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (km is null) return;
        km.UseAnimGraph = true;
        // Position first (no rotation) — same DispatchSpawn-before-SetModel
        // ordering as the T ghost. Rotation is applied after
        // DispatchSpawn+SetModel because prop_dynamic entities ignore
        // pre-spawn transforms; the T ghost gets re-teleported every tick
        // from AdvanceMember so the issue doesn't show up there.
        km.Teleport(killer.KillerPosition);
        km.DispatchSpawn();
        km.SetModel(killer.KillerModelName);
        // Zero pitch/roll — body yaw only, matching how the engine orients
        // a standing player (AbsRotation.X/Z are near-zero but not always).
        var bodyYaw = new QAngle(0f, killer.KillerRotation.Y, 0f);
        km.Teleport(killer.KillerPosition, bodyYaw);
        ch.KillerGhost = km;

        // Red glow companion — mirror of the T's green glow, so the CT reads
        // at a glance when they pop in at the kill moment.
        var glow = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        if (glow is not null)
        {
            glow.DispatchSpawn();
            glow.SetModel(killer.KillerModelName);
            glow.Spawnflags = 256u;
            glow.Glow.GlowColorOverride = Color.Red;
            glow.Glow.GlowRange = 0;
            glow.Glow.GlowTeam = -1;
            glow.Glow.GlowType = 3;
            glow.Glow.GlowRangeMin = 0;
            glow.Render = Color.FromArgb(5, 255, 255, 255);
            glow.AcceptInput("FollowEntity", km, glow, "!activator");
            ch.KillerGlow = glow;
        }
    }

    // Called from the sampler's timer. Advances each channel's frame index,
    // transitions through Finished (last frame drawn, killer ghost spawned,
    // kill-shot beam drawn) and Despawned (entities killed) states, holding
    // LingerSeconds between the two so the kill moment is readable.
    //
    // Channels with StartFrameDelay > 0 sit idle until _frameIndex catches up,
    // then spawn lazily. This is what end-aligns multiple channels on the same
    // wall-clock kill moment (the killer companion is usually delayed only
    // when the killer's history is shorter than the victim's, which is rare
    // but does happen if the killer just respawned).
    public void Tick()
    {
        if (!IsPlaying) return;
        var now = DateTime.UtcNow;
        var elapsed = (float)(now - _startedAt).TotalSeconds;
        var anyAlive = false;

        foreach (var ch in _channels)
        {
            if (ch.Despawned) continue;

            var localFrame = _frameIndex - ch.StartFrameDelay;
            if (localFrame < 0)
            {
                // Channel hasn't started yet — its first frame is later than
                // the earliest channel's so it begins partway into the replay.
                anyAlive = true;
                continue;
            }

            if (!ch.Spawned)
            {
                SpawnMember(ch);
                ch.Spawned = true;
            }

            if (!ch.Finished)
            {
                if (localFrame >= ch.Frames.Length)
                {
                    FinishMember(ch, elapsed);
                }
                else
                {
                    AdvanceMember(ch, localFrame, elapsed);
                    if (localFrame == ch.Frames.Length - 1) FinishMember(ch, elapsed);
                }
            }

            if (ch.Finished && ch.FinishedAt is DateTime t
                && (now - t).TotalSeconds >= _lingerSeconds())
            {
                DespawnMember(ch);
            }

            if (!ch.Despawned) anyAlive = true;
        }
        _frameIndex++;
        if (!anyAlive) IsPlaying = false;
    }

    private void AdvanceMember(MemberChannel ch, int frameIdx, float elapsed)
    {
        var frame = ch.Frames[frameIdx];
        // Victim's chat-line announcer (T fired AK / threw HE / swung knife).
        // Suppress for killer-companion channels — they don't carry a victim
        // event log, and "killer fired" lines would just clutter the feed.
        if (!ch.IsKillerCompanion)
            MaybeAnnounceVictimActions(ch, frame.TimeSinceRoundStart, elapsed);
        var ghost = ch.Ghost;
        if (ghost is null || !ghost.IsValid) return;

        // SetAnimation used to live here, switching between
        // {walk,crouch}_new_rifle_stopped and idle_for_turns_{stand,crouch}_knife
        // per frame. Modern CS2 player models are animgraph-driven and don't
        // embed those sequences — the engine warned on every call. We now
        // just let the animgraph render its default idle (UseAnimGraph=true
        // on spawn). Setting individual graph control params from a plugin
        // isn't exposed by CSSharp today, so we can't drive walk/crouch/aim
        // states — the ghost stands, but Teleport below still tracks the
        // captured position/orientation/velocity correctly.
        // Tint by victim's highest weapon tier — yellow = pistol, red =
        // primary, default model color = unarmed. Killer companions render in
        // the model's natural color (the red glow already marks them as CT).
        var tierRgb = ch.IsKillerCompanion
            ? (frame.ModelRenderColor.R, frame.ModelRenderColor.G, frame.ModelRenderColor.B)
            : ch.Entry.VictimTier switch
            {
                WeaponTier.Primary => (R: (byte)255, G: (byte)80, B: (byte)80),
                WeaponTier.Pistol => (R: (byte)255, G: (byte)220, B: (byte)80),
                _ => (frame.ModelRenderColor.R, frame.ModelRenderColor.G, frame.ModelRenderColor.B),
            };
        ghost.RenderMode = RenderMode_t.kRenderTransAlpha;
        ghost.Render = Color.FromArgb((int)(255 * 0.8), tierRgb.R, tierRgb.G, tierRgb.B);
        Utilities.SetStateChanged(ghost, "CBaseModelEntity", "m_clrRender");
        // Body yaw only — zero pitch/roll so the model stays upright instead
        // of tilting with the view (live players hold upright bodies).
        var bodyYaw = new QAngle(0f, frame.PlayerRotation.Y, 0f);
        ghost.Teleport(frame.Location, bodyYaw, frame.Velocity);
        ghost.DispatchSpawn();

        // Look beam.
        var eyeHeight = frame.IsCrouching ? 65f * 0.8f : 65f;
        var eyeStart = new Vector(frame.Location.X, frame.Location.Y, frame.Location.Z + eyeHeight);
        var lookEnd = RayEnd(eyeStart, frame.ViewAngles, 50f);
        // Slight start offset so the beam isn't inside the head.
        var shotStart = RayEnd(eyeStart, frame.ViewAngles, 5f);

        // env_beam's start position is baked at DispatchSpawn — Teleport on an
        // already-spawned beam doesn't reliably move the start endpoint. Kill
        // and recreate each frame; cheap (~once per sample per member during
        // playback, well under 1% of tick budget).
        TryKill(ch.LookBeam);
        ch.LookBeam = BeamHelpers.CreateBeamBetweenPoints(
            shotStart, lookEnd, Color.Lime, 1.0f);

        if (frame.ShotDirection is not null)
        {
            var shotEnd = RayEnd(eyeStart, frame.ShotDirection, 1500f);
            var shotBeam = BeamHelpers.CreateBeamBetweenPoints(shotStart, shotEnd, Color.Red, 0.3f);
            if (shotBeam is not null) ch.ShotBeams.Add(shotBeam);
        }
    }

    // Walks ch.NextEventIdx forward over any ShotFired events that happened
    // at-or-before the current replay frame's timestamp. Emits one chat line
    // when the weapon differs from the last announced one (so sprays and
    // burst fire don't spam). Melee swings and grenade throws also come
    // through as ShotFired (the recorder logs them all); formatting branches
    // in the plugin-level formatter by weapon-name category.
    private void MaybeAnnounceVictimActions(MemberChannel ch, float frameAt, float elapsed)
    {
        if (_formatShotLine is null) return;
        var events = ch.Entry.Events;
        if (events is null) return;

        while (ch.NextEventIdx < events.Count && events[ch.NextEventIdx].At <= frameAt)
        {
            if (events[ch.NextEventIdx] is ShotFired sf)
            {
                if (!string.Equals(sf.Weapon, ch.LastAnnouncedWeapon, StringComparison.Ordinal))
                {
                    Server.PrintToChatAll(_formatShotLine(elapsed, ch.Entry.VictimName, sf.Weapon));
                    ch.LastAnnouncedWeapon = sf.Weapon;
                }
            }
            ch.NextEventIdx++;
        }
    }

    private void FinishMember(MemberChannel ch, float elapsed)
    {
        if (ch.Finished) return;
        ch.Finished = true;
        ch.FinishedAt = DateTime.UtcNow;

        // Killer-companion channels carry no kill of their own. Mark finished
        // and let the linger window despawn them; the kill-shot beam + static
        // killer-ghost spawn belong to the victim channel.
        if (ch.IsKillerCompanion) return;

        // Spawn the static killer ghost only when no animated killer companion
        // exists — the companion already represents the CT throughout playback.
        if (!ch.HasAnimatedKiller)
            SpawnKillerGhost(ch);

        // Draw kill-shot beam + chat line on the final frame transition.
        // Skip suicides (killer slot == victim slot) and world/fall kills
        // (KillerAt null entirely).
        if (!ch.KillShotDrawn && ch.Entry.KillerAt is { } killer
            && killer.KillerSlot != ch.Entry.Slot)
        {
            var last = ch.Frames.Length > 0 ? ch.Frames[^1] : null;
            if (last is not null)
            {
                var eyeHeight = last.IsCrouching ? 65f * 0.8f : 65f;
                var victimHead = new Vector(
                    last.Location.X, last.Location.Y, last.Location.Z + eyeHeight);
                var killerHead = new Vector(
                    killer.KillerPosition.X,
                    killer.KillerPosition.Y,
                    killer.KillerPosition.Z + 65f);
                var beam = BeamHelpers.CreateBeamBetweenPoints(
                    killerHead, victimHead, Color.Blue, 0.3f);
                if (beam is not null) ch.ShotBeams.Add(beam);

                var victimName = ch.Entry.VictimName;
                var killerName = killer.KillerName;
                var weaponLabel = string.IsNullOrEmpty(killer.Weapon) ? "?" : killer.Weapon;
                Server.PrintToChatAll(_formatKillLine(elapsed, killerName, victimName, weaponLabel));
            }
            ch.KillShotDrawn = true;
        }
    }

    private void DespawnMember(MemberChannel ch)
    {
        if (ch.Despawned) return;
        ch.Despawned = true;
        TryKill(ch.Ghost);
        ch.Ghost = null;
        TryKill(ch.GhostGlow);
        ch.GhostGlow = null;
        TryKill(ch.LookBeam);
        ch.LookBeam = null;
        TryKill(ch.KillerGhost);
        ch.KillerGhost = null;
        TryKill(ch.KillerGlow);
        ch.KillerGlow = null;
    }

    public void Stop()
    {
        IsPlaying = false;
        foreach (var ch in _channels)
        {
            foreach (var b in ch.ShotBeams) TryKill(b);
            ch.ShotBeams.Clear();
            DespawnMember(ch);
            ch.Finished = true;
        }
    }

    private static void TryKill(CEntityInstance? ent)
    {
        if (ent is null) return;
        if (!ent.IsValid) return;
        ent.AcceptInput("Kill");
    }

    private static Vector RayEnd(Vector start, QAngle angles, float dist)
    {
        var pitchRad = angles.X * Math.PI / 180.0;
        var yawRad = angles.Y * Math.PI / 180.0;
        var cosPitch = Math.Cos(pitchRad);
        return new Vector(
            start.X + (float)(Math.Cos(yawRad) * cosPitch * dist),
            start.Y + (float)(Math.Sin(yawRad) * cosPitch * dist),
            start.Z - (float)(Math.Sin(pitchRad) * dist));
    }
}
