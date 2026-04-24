namespace Menn.Utils;

// Resolves admin command <target> arguments:
//   - SteamID64 (64-bit numeric) → direct hit, works online or offline.
//   - "#slot" (e.g. "#3") → online only.
//   - case-insensitive substring of visible name → online only.
//
// Ambiguous substring or zero-match returns a TargetResolution with the
// candidate list so command handlers can reply with something useful
// (e.g. "2 matches — #3 Alice, #7 Alicia").
public enum TargetResolutionKind
{
    Resolved,
    Ambiguous,
    NotFound,
}

public sealed record TargetResolution(
    TargetResolutionKind Kind,
    ulong SteamId,
    int Slot,                                      // -1 when SteamId resolved offline or not applicable
    IReadOnlyList<PlayerCache.Entry> Candidates);

public sealed class TargetResolver
{
    private readonly PlayerCache _players;

    public TargetResolver(PlayerCache players)
    {
        _players = players;
    }

    public TargetResolution Resolve(string? raw)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length == 0)
            return new TargetResolution(TargetResolutionKind.NotFound, 0, -1, Array.Empty<PlayerCache.Entry>());

        // SteamID64 — a 17-digit positive number starting with 765.
        // ulong.TryParse accepts 18446744073709551615 max; we only tighten to
        // "looks like a SteamID64" for error hinting, not strict validation.
        // If the numeric id maps to a currently-online slot we include the
        // entry in the candidates; otherwise we return Resolved with an empty
        // list so callers can still act on the SteamId (e.g. offline ban).
        if (ulong.TryParse(s, out var sid) && sid > 0)
        {
            foreach (var e in _players.All())
            {
                if (e.SteamId == sid)
                    return new TargetResolution(TargetResolutionKind.Resolved, sid, e.Slot, new[] { e });
            }
            return new TargetResolution(TargetResolutionKind.Resolved, sid, -1, Array.Empty<PlayerCache.Entry>());
        }

        // #slot — online only
        if (s.StartsWith('#') && int.TryParse(s[1..], out var slot))
        {
            var entry = _players.Get(slot);
            if (entry is not null)
                return new TargetResolution(TargetResolutionKind.Resolved, entry.SteamId, entry.Slot, new[] { entry });
            return new TargetResolution(TargetResolutionKind.NotFound, 0, -1, Array.Empty<PlayerCache.Entry>());
        }

        // Case-insensitive substring on visible name, online only.
        var matches = new List<PlayerCache.Entry>();
        foreach (var e in _players.All())
            if (e.Name.Contains(s, StringComparison.OrdinalIgnoreCase))
                matches.Add(e);

        return matches.Count switch
        {
            0 => new TargetResolution(TargetResolutionKind.NotFound, 0, -1, Array.Empty<PlayerCache.Entry>()),
            1 => new TargetResolution(TargetResolutionKind.Resolved, matches[0].SteamId, matches[0].Slot, matches),
            _ => new TargetResolution(TargetResolutionKind.Ambiguous, 0, -1, matches),
        };
    }
}
