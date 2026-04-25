using CounterStrikeSharp.API.Modules.Utils;
using Postmortem.Replay;

namespace Postmortem;

// Highest-tier weapon the victim was carrying at death. Drives the replay
// ghost tint (NotArmed = default model color, Pistol = yellow, Primary = red)
// so an admin can see at a glance whether the kill was on someone defenseless.
// Ordered numerically so a "max so far" reduce works.
public enum WeaponTier
{
    NotArmed = 0,
    Pistol = 1,
    Primary = 2,
}

// Per-death record. MovementHistory / Events / KillerAt are null when
// pm_replay_enabled=false at the time of death. The stack works regardless —
// callers that want replay data check for null.
//
// KillerMovementHistory is a non-destructive snapshot of the *killer's*
// recent ring buffer (taken at the same instant as MovementHistory) so the
// CT can be replayed alongside the T. Null when sampling was off, or the
// kill was a suicide (KillerSlot == Slot), or the killer's buffer was empty.
//
// DeathPosition / DeathAngles are captured unconditionally (regardless of
// pm_replay_enabled) because respawn-at-death doesn't need a full movement
// window — just where the body fell. Null only if the victim's pawn couldn't
// be read at death time (disconnect race, etc).
//
// Id is assigned by DeathStack.Push (stable, monotonic within a round). User
// types it in commands like `!replay 7` or `!sres #7`. Reset on round_start /
// map_start so numbers stay small.
public sealed record DeathEntry(
    int Slot,
    string VictimName,
    DateTime At,
    MovementFrame[]? MovementHistory,
    List<ReplayEvent>? Events,
    KillerSnapshot? KillerAt,
    Vector? DeathPosition,
    QAngle? DeathAngles,
    WeaponTier VictimTier = WeaponTier.NotArmed,
    MovementFrame[]? KillerMovementHistory = null)
{
    public int Id { get; init; }
}

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
    private int _nextId = 1;
    public int Count => _order.Count;

    public int Evictions { get; private set; }
    public int PeakCount { get; private set; }

    // Stamps a stable Id on the entry and pushes it newest-last. Returns the
    // pushed entry (with Id populated) so the caller can log/echo it.
    public DeathEntry Push(DeathEntry entry, int cap)
    {
        var stamped = entry with { Id = _nextId++ };
        if (_order.Count >= cap)
        {
            // FIFO: drop oldest.
            _order.RemoveAt(0);
            Evictions++;
        }
        _order.Add(stamped);
        if (_order.Count > PeakCount) PeakCount = _order.Count;
        return stamped;
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

    // Reset the id counter — called from round_start / map_start so
    // numbers don't grow across rounds. Stack itself is also cleared at
    // those points; resetting is safe because no live references survive.
    public void ResetIds()
    {
        _nextId = 1;
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

    // Pop the entry with the given stable Id, or null if not in the stack.
    // Used by !sres #N and !replay <id>.
    public DeathEntry? PopById(int id)
    {
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            if (_order[i].Id == id)
            {
                var entry = _order[i];
                _order.RemoveAt(i);
                return entry;
            }
        }
        return null;
    }

    // Find the entry with the given stable Id without removing it.
    public DeathEntry? FindById(int id)
    {
        for (var i = 0; i < _order.Count; i++)
            if (_order[i].Id == id) return _order[i];
        return null;
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

    // Find the chain-linked group containing the death with stable Id `id`,
    // and pop every member. Returns newest-first or empty if no entry matches.
    // Used by !sresevent <id>.
    public IReadOnlyList<DeathEntry> PopGroupContainingId(int id, double gapSeconds)
    {
        if (_order.Count == 0) return Array.Empty<DeathEntry>();
        var found = -1;
        for (var i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id) { found = i; break; }
        }
        if (found < 0) return Array.Empty<DeathEntry>();

        var (lo, hi) = ExpandGroupBounds(found, gapSeconds);
        var take = hi - lo + 1;
        var popped = new DeathEntry[take];
        // Newest-first: _order is oldest-first, so iterate hi → lo.
        for (var k = 0; k < take; k++)
            popped[k] = _order[hi - k];
        _order.RemoveRange(lo, take);
        return popped;
    }

    // Find the chain-linked group containing the death with stable Id `id`
    // without removing entries. Used by !replayevent <id>. Newest-first.
    public IReadOnlyList<DeathEntry> FindGroupContainingId(int id, double gapSeconds)
    {
        if (_order.Count == 0) return Array.Empty<DeathEntry>();
        var found = -1;
        for (var i = 0; i < _order.Count; i++)
        {
            if (_order[i].Id == id) { found = i; break; }
        }
        if (found < 0) return Array.Empty<DeathEntry>();

        var (lo, hi) = ExpandGroupBounds(found, gapSeconds);
        var take = hi - lo + 1;
        var snap = new DeathEntry[take];
        for (var k = 0; k < take; k++)
            snap[k] = _order[hi - k];
        return snap;
    }

    // Walks left and right from `seed` while consecutive entries are within
    // gap (chain-link refresh). Used by both Pop/Find Group variants.
    private (int lo, int hi) ExpandGroupBounds(int seed, double gapSeconds)
    {
        var lo = seed;
        while (lo > 0 && (_order[lo].At - _order[lo - 1].At).TotalSeconds <= gapSeconds) lo--;
        var hi = seed;
        while (hi < _order.Count - 1 && (_order[hi + 1].At - _order[hi].At).TotalSeconds <= gapSeconds) hi++;
        return (lo, hi);
    }
}
