using CounterStrikeSharp.API.Modules.Utils;
using Postmortem.Replay;

namespace Postmortem;

// Per-death record. MovementHistory / Events / KillerAt are null when
// pm_replay_enabled=false at the time of death. The stack works regardless —
// callers that want replay data check for null.
//
// DeathPosition / DeathAngles are captured unconditionally (regardless of
// pm_replay_enabled) because respawn-at-death doesn't need a full movement
// window — just where the body fell. Null only if the victim's pawn couldn't
// be read at death time (disconnect race, etc).
public sealed record DeathEntry(
    int Slot,
    string VictimName,
    DateTime At,
    MovementFrame[]? MovementHistory,
    List<ReplayEvent>? Events,
    KillerSnapshot? KillerAt,
    Vector? DeathPosition,
    QAngle? DeathAngles);

// Round-bounded LIFO of recent deaths.
//
// Design change vs MVP (2026-04-24): dropped the per-slot dedup. A player can
// die → get respawned (by !pmres, admin, freeday, warden) → die again within
// the same round. Both deaths are independently meaningful forensically; the
// old dedup silently lost the earlier one. Each Push unconditionally creates
// a new entry.
//
// Remove(slot) on disconnect removes ALL entries for that slot (not just the
// latest), so a disconnecting player doesn't leak their snapshots.
//
// FIFO eviction: when Count == cap and a new death pushes, the oldest entry
// is dropped (evictions counter incremented). Extreme-case safety cap so the
// stack can't balloon if !pmres isn't being called.
//
// Single-threaded — all mutation is from game-thread event handlers/commands.
public sealed class DeathStack
{
    private readonly List<DeathEntry> _order = new();
    public int Count => _order.Count;

    public int Evictions { get; private set; }
    public int PeakCount { get; private set; }

    public void Push(DeathEntry entry, int cap)
    {
        if (_order.Count >= cap)
        {
            // FIFO: drop oldest.
            _order.RemoveAt(0);
            Evictions++;
        }
        _order.Add(entry);
        if (_order.Count > PeakCount) PeakCount = _order.Count;
    }

    public int Remove(int slot)
    {
        var removed = 0;
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            if (_order[i].Slot == slot)
            {
                _order.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    public void Clear()
    {
        _order.Clear();
    }

    // Debug: newest-first snapshot (doesn't mutate).
    public IReadOnlyList<DeathEntry> SnapshotNewestFirst()
    {
        if (_order.Count == 0) return Array.Empty<DeathEntry>();
        var snap = new DeathEntry[_order.Count];
        for (var i = 0; i < _order.Count; i++)
            snap[i] = _order[_order.Count - 1 - i];
        return snap;
    }

    // Pop the last N individual deaths, newest-first. Used by !pmres (no
    // grouping).
    public IReadOnlyList<DeathEntry> PopLastN(int n)
    {
        if (n <= 0 || _order.Count == 0) return Array.Empty<DeathEntry>();
        var take = Math.Min(n, _order.Count);
        var popped = new DeathEntry[take];
        for (var i = 0; i < take; i++)
            popped[i] = _order[_order.Count - 1 - i];
        _order.RemoveRange(_order.Count - take, take);
        return popped;
    }

    // Group entries newest-first using chain-based grouping with refreshing
    // gap (read-only, no mutation). Each group is newest-first; groups
    // themselves are newest-first (group[0] = the newest event).
    public IReadOnlyList<IReadOnlyList<DeathEntry>> SnapshotGroups(double gapSeconds)
    {
        if (_order.Count == 0) return Array.Empty<IReadOnlyList<DeathEntry>>();
        var groups = new List<IReadOnlyList<DeathEntry>>();
        var i = _order.Count - 1;
        while (i >= 0)
        {
            var group = new List<DeathEntry> { _order[i] };
            var anchor = _order[i].At;
            i--;
            while (i >= 0 && (anchor - _order[i].At).TotalSeconds <= gapSeconds)
            {
                group.Add(_order[i]);
                anchor = _order[i].At;
                i--;
            }
            groups.Add(group);
        }
        return groups;
    }

    // Pop the event at index groupIndex (0-based from newest) using the same
    // chain-based grouping. Used by !pmresevent. Returns the event's entries
    // (newest-first) or empty if groupIndex is out of range.
    public IReadOnlyList<DeathEntry> PopGroup(int groupIndex, double gapSeconds)
    {
        if (groupIndex < 0 || _order.Count == 0) return Array.Empty<DeathEntry>();
        // Walk groups newest-first; when we reach groupIndex, that group is
        // our target.
        var i = _order.Count - 1;
        var currentGroupIdx = 0;
        while (i >= 0)
        {
            var groupEndExclusive = i + 1;  // _order index range (startInclusive, groupEndExclusive]
            var anchor = _order[i].At;
            i--;
            while (i >= 0 && (anchor - _order[i].At).TotalSeconds <= gapSeconds)
            {
                anchor = _order[i].At;
                i--;
            }
            var groupStartInclusive = i + 1;

            if (currentGroupIdx == groupIndex)
            {
                var take = groupEndExclusive - groupStartInclusive;
                var popped = new DeathEntry[take];
                // popped newest-first: _order[groupEnd-1] .. _order[groupStart]
                for (var k = 0; k < take; k++)
                    popped[k] = _order[groupEndExclusive - 1 - k];
                _order.RemoveRange(groupStartInclusive, take);
                return popped;
            }
            currentGroupIdx++;
        }
        return Array.Empty<DeathEntry>();
    }
}
