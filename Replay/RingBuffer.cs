using CounterStrikeSharp.API.Modules.Utils;

namespace Postmortem.Replay;

// Fixed-capacity ring buffer for MovementFrame references, O(1) push with
// wrap-around (no List.RemoveAt(0) shift). Single-threaded — all mutation
// comes from the game-thread sampler or command callbacks.
//
// The backing array holds one MovementFrame per slot that is refilled in place
// via Write(index, ...). Refilling rather than allocating new MovementFrame
// objects per tick is what keeps the hot path alloc-free.
public sealed class RingBuffer
{
    private readonly MovementFrame[] _slots;
    private int _head;   // write index (next slot to overwrite)
    private int _count;

    public int Capacity { get; }
    public int Count => _count;

    public RingBuffer(int capacity)
    {
        Capacity = capacity;
        _slots = new MovementFrame[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new MovementFrame();
    }

    // Returns the slot to fill. Caller writes fields directly into the returned
    // frame. When the buffer is full, the oldest slot is reused.
    public MovementFrame NextSlot()
    {
        var slot = _slots[_head];
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        return slot;
    }

    // Frame at index i within the live buffer (0 = oldest, Count-1 = newest).
    // Direct access avoids an extra allocation — caller treats it as read-only.
    public MovementFrame At(int i)
    {
        if (i < 0 || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
        var start = _count < Capacity ? 0 : _head;
        return _slots[(start + i) % Capacity];
    }

    // Copy live frames (oldest → newest) into a freshly-allocated array and
    // return it. Size is exactly Count. The ring buffer stays unchanged; callers
    // that want a "fresh window" after snapshotting should call Clear().
    // Vector/QAngle are copied by value (not reference) because the ring
    // buffer's live slots keep getting reused on subsequent samples — a
    // reference copy would mean the snapshot's coordinates mutate as the
    // sampler keeps writing. Cheap: ~Count × 4 small allocations per death.
    public MovementFrame[] SnapshotCopy()
    {
        if (_count == 0) return Array.Empty<MovementFrame>();
        var copy = new MovementFrame[_count];
        var start = _count < Capacity ? 0 : _head;
        for (var i = 0; i < _count; i++)
        {
            var src = _slots[(start + i) % Capacity];
            copy[i] = new MovementFrame
            {
                Location = new Vector(src.Location.X, src.Location.Y, src.Location.Z),
                ViewAngles = new QAngle(src.ViewAngles.X, src.ViewAngles.Y, src.ViewAngles.Z),
                PlayerRotation = new QAngle(src.PlayerRotation.X, src.PlayerRotation.Y, src.PlayerRotation.Z),
                Velocity = new Vector(src.Velocity.X, src.Velocity.Y, src.Velocity.Z),
                ActiveWeaponDesignerName = src.ActiveWeaponDesignerName,
                ModelRenderColor = src.ModelRenderColor,
                IsCrouching = src.IsCrouching,
                ModelName = src.ModelName,
                ShotDirection = src.ShotDirection is null
                    ? null
                    : new QAngle(src.ShotDirection.X, src.ShotDirection.Y, src.ShotDirection.Z),
                JbRoleFlags = src.JbRoleFlags,
                TimeSinceRoundStart = src.TimeSinceRoundStart,
            };
        }
        return copy;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    // Index of the most recently written frame, or -1 if empty. Used by the
    // weapon-fire hook to stamp ShotDirection onto the current-tick frame.
    public MovementFrame? LastOrDefault()
    {
        if (_count == 0) return null;
        var idx = (_head - 1 + Capacity) % Capacity;
        return _slots[idx];
    }
}
