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
    private readonly Func<string, string, string, string> _formatKillLine;
    // Formatter for per-action chat lines ("T fired {weapon}", "T threw
    // {weapon}", "T swung a knife"). Called from AdvanceMember when we cross
    // an event boundary and the weapon differs from the last announced one.
    private readonly Func<string, string, string>? _formatShotLine;
    private readonly List<MemberChannel> _channels;
    private readonly DateTime _startedAt;
    private int _frameIndex;
    public bool IsPlaying { get; private set; } = true;
    public int EventId { get; }
    public int MemberCount => _channels.Count;
    public int CurrentFrame => _frameIndex;
    public int TotalFrames { get; }

    private sealed class MemberChannel
    {
        public DeathEntry Entry = default!;
        public MovementFrame[] Frames = Array.Empty<MovementFrame>();
        public CDynamicProp? Ghost;
        public CBaseModelEntity? GhostGlow;  // the nice green glow for the T
        public CDynamicProp? KillerGhost;    // spawned at kill moment, not replay start
        public CBaseModelEntity? KillerGlow; // red glow companion for the CT
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
    }

    public MovementReplay(
        int eventId,
        IReadOnlyList<DeathEntry> members,
        ILogger logger,
        Func<string, string, string, string> formatKillLine,
        Func<float> lingerSeconds,
        Func<string, string, string>? formatShotLine = null)
    {
        EventId = eventId;
        _logger = logger;
        _formatKillLine = formatKillLine;
        _formatShotLine = formatShotLine;
        _lingerSeconds = lingerSeconds;
        _channels = new List<MemberChannel>(members.Count);
        _startedAt = DateTime.UtcNow;

        var maxFrames = 0;
        foreach (var m in members)
        {
            var frames = m.MovementHistory ?? Array.Empty<MovementFrame>();
            if (frames.Length == 0) continue;
            maxFrames = Math.Max(maxFrames, frames.Length);
            _channels.Add(new MemberChannel
            {
                Entry = m,
                Frames = frames,
            });
        }
        TotalFrames = maxFrames;

        foreach (var ch in _channels) SpawnMember(ch);
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
        // UseAnimGraph=false (yappers pattern) forces manual SetAnimation
        // calls — which only works if the model still embeds the expected
        // named sequences. Modern CS2 player models are graph-driven
        // (animation/graphs/worldmodel/worldmodel.vnmgraph) and carry
        // almost no raw sequences, so SetAnimation just logs warnings.
        // With the graph enabled and no control parameters fed, the prop
        // runs the graph's default state (idle/stand).
        ghost.UseAnimGraph = true;
        ghost.Teleport(ch.Frames[0].Location);
        // yappers' order — DispatchSpawn before SetModel — produces a benign
        // "no model name" warning from the engine on first frame. Reversing
        // it breaks the prop entirely (no visible entity). Left as-is.
        ghost.DispatchSpawn();
        ghost.SetModel(modelName);
        ch.Ghost = ghost;

        // Green glow companion prop — yappers' recipe. Follows the main ghost
        // so the T pops visually without tinting the main model.
        var glow = Utilities.CreateEntityByName<CBaseModelEntity>("prop_dynamic");
        if (glow is not null)
        {
            glow.DispatchSpawn();
            glow.SetModel(modelName);
            glow.Spawnflags = 256u;
            glow.Glow.GlowColorOverride = Color.Green;
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
        // Position first (no rotation) — same yappers ordering as the T ghost.
        // Rotation applied after DispatchSpawn+SetModel because prop_dynamic
        // entities ignore pre-spawn transforms; the T ghost gets re-teleported
        // every tick from AdvanceMember so the issue doesn't show up there.
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
    public void Tick()
    {
        if (!IsPlaying) return;
        var now = DateTime.UtcNow;
        var anyAlive = false;

        foreach (var ch in _channels)
        {
            if (ch.Despawned) continue;

            if (!ch.Finished)
            {
                if (_frameIndex >= ch.Frames.Length)
                {
                    FinishMember(ch);
                }
                else
                {
                    AdvanceMember(ch, _frameIndex);
                    if (_frameIndex == ch.Frames.Length - 1) FinishMember(ch);
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

    private void AdvanceMember(MemberChannel ch, int frameIdx)
    {
        var frame = ch.Frames[frameIdx];
        MaybeAnnounceVictimActions(ch, frame.TimeSinceRoundStart);
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
        ghost.RenderMode = RenderMode_t.kRenderTransAlpha;
        ghost.Render = Color.FromArgb(
            (int)(255 * 0.8),
            frame.ModelRenderColor.R,
            frame.ModelRenderColor.G,
            frame.ModelRenderColor.B);
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
            shotStart, lookEnd, Color.FromArgb(255, 255, 0, 255), 0.3f);

        if (frame.ShotDirection is not null)
        {
            var shotEnd = RayEnd(eyeStart, frame.ShotDirection, 200f);
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
    private void MaybeAnnounceVictimActions(MemberChannel ch, float frameAt)
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
                    Server.PrintToChatAll(_formatShotLine(ch.Entry.VictimName, sf.Weapon));
                    ch.LastAnnouncedWeapon = sf.Weapon;
                }
            }
            ch.NextEventIdx++;
        }
    }

    private void FinishMember(MemberChannel ch)
    {
        if (ch.Finished) return;
        ch.Finished = true;
        ch.FinishedAt = DateTime.UtcNow;

        // Spawn the killer ghost at the kill moment — it's not meant to hang
        // around throughout the replay.
        SpawnKillerGhost(ch);

        // Draw kill-shot beam + chat line on the final frame transition.
        // Skip suicides (killer slot == victim slot).
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
                Server.PrintToChatAll(_formatKillLine(killerName, victimName, weaponLabel));
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
