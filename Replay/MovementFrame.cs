using System.Drawing;
using CounterStrikeSharp.API.Modules.Utils;

namespace Postmortem.Replay;

// Per-tick sample captured while pm_replay_enabled=true.
// Reference type (class) because CSSharp Vector/QAngle are already reference
// types; wrapping them in a struct gains nothing — the heavy fields still
// allocate separately. Backing `MovementFrame[]` arrays hold N references.
//
// String fields are interned via StringIntern to avoid per-sample allocation
// from PlayerName / DesignerName lookups; typical map has ~2 model names + ~20
// weapon designer names = ~22 unique strings interned total.
public sealed class MovementFrame
{
    public Vector Location = new();
    public QAngle ViewAngles = new();
    public QAngle PlayerRotation = new();
    public Vector Velocity = new();
    public string? ActiveWeaponDesignerName;   // interned
    public Color ModelRenderColor;
    public bool IsCrouching;
    public string? ModelName;                  // interned
    public QAngle? ShotDirection;              // non-null on frames the player fired
    public byte JbRoleFlags;                   // reserved v1
    public float TimeSinceRoundStart;
}
