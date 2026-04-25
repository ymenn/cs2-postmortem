using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Postmortem.Replay;

// CEnvBeam creation helper. env_beam is a point-to-point beam entity:
// Teleport sets start, EndPos sets end, Render sets color, Width/EndWidth
// set thickness.
//
// Killed with AcceptInput("Kill"). Callers manage lifetime.
public static class BeamHelpers
{
    public static CEnvBeam? CreateBeamBetweenPoints(
        Vector start, Vector end, Color color, float thickness)
    {
        var beam = Utilities.CreateEntityByName<CEnvBeam>("env_beam");
        if (beam is null) return null;

        beam.Render = color;
        beam.Width = thickness;
        beam.EndWidth = thickness;

        beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;
        beam.DispatchSpawn();
        return beam;
    }
}
