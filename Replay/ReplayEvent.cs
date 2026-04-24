using CounterStrikeSharp.API.Modules.Utils;

namespace Postmortem.Replay;

// Sparse event log per player, runs alongside the frame ring buffer.
// `At` is seconds since round start (same timeline MovementFrame uses).
public abstract record ReplayEvent(float At);

public sealed record ShotFired(float At, string Weapon) : ReplayEvent(At);

public sealed record DamageDealt(
    float At, int TargetSlot, int Amount, string Weapon, byte HitGroup) : ReplayEvent(At);

public sealed record DamageTaken(
    float At, int SourceSlot, int Amount, string Weapon, byte HitGroup) : ReplayEvent(At);

public sealed record WeaponPickup(float At, string Weapon) : ReplayEvent(At);

// Snapshot of the killer at the moment of death. Attached to the victim's
// DeathEntry so the replay can draw a kill-shot beam from killer → victim.
public sealed record KillerSnapshot(
    ulong KillerSteamId,
    int KillerSlot,           // -1 if killer couldn't be resolved
    string KillerName,
    Vector KillerPosition,
    QAngle KillerRotation,
    QAngle KillerViewAngles,
    string? KillerModelName,
    string Weapon,
    byte HitGroup,
    int Damage,
    byte KillerJbRoleFlags);  // reserved v1
