using System.Collections.Concurrent;
using System.Diagnostics;

namespace Menn.Utils;

// Per-op latency tracker for event handlers + hot paths that run on the game
// thread. Goal: spot handlers stealing frames from the server tick before
// players feel them as a freeze.
//
// Overhead budget: ~100–200 ns per Measure() call uncontended. Uses
// Stopwatch.GetTimestamp() (a single RDTSC on x64) + a bounded buffer per op
// under a short-held lock. MaxSamplesPerOp caps memory at 4 KB/op and drops
// further samples in the same flush window.
//
// FlushAndReset() is the canonical "drain the window" call — used by the
// hourly health timer in gatekeeper. Snapshot(reset=false) peeks without
// resetting (for live /status commands). Snapshot(opName) returns a single
// op's stats without touching the others.
public sealed record PerfStats(
    string OpName,
    int Count,
    long TotalMicros,
    int P50Micros,
    int P95Micros,
    int P99Micros,
    int MaxMicros,
    int DroppedSamples);

public sealed class PerfTracker
{
    private const int MaxSamplesPerOp = 1024;
    private readonly ConcurrentDictionary<string, Accumulator> _ops = new();
    private static readonly double TicksToMicros = 1_000_000.0 / Stopwatch.Frequency;

    public void Measure(string opName, Action action)
    {
        var start = Stopwatch.GetTimestamp();
        try { action(); }
        finally { Record(opName, Stopwatch.GetTimestamp() - start); }
    }

    public T Measure<T>(string opName, Func<T> fn)
    {
        var start = Stopwatch.GetTimestamp();
        try { return fn(); }
        finally { Record(opName, Stopwatch.GetTimestamp() - start); }
    }

    public async Task MeasureAsync(string opName, Func<Task> fn)
    {
        var start = Stopwatch.GetTimestamp();
        try { await fn(); }
        finally { Record(opName, Stopwatch.GetTimestamp() - start); }
    }

    public async Task<T> MeasureAsync<T>(string opName, Func<Task<T>> fn)
    {
        var start = Stopwatch.GetTimestamp();
        try { return await fn(); }
        finally { Record(opName, Stopwatch.GetTimestamp() - start); }
    }

    private void Record(string opName, long ticks)
    {
        var acc = _ops.GetOrAdd(opName, _ => new Accumulator());
        var micros = (int)Math.Min(ticks * TicksToMicros, int.MaxValue);
        acc.Add(micros);
    }

    // Drain every op's window into stats and reset the accumulators.
    public IReadOnlyList<PerfStats> FlushAndReset() => SnapshotAll(reset: true);

    // Peek / drain every op. `reset=true` is equivalent to FlushAndReset();
    // `reset=false` is a live peek that doesn't clear accumulators.
    public IReadOnlyList<PerfStats> Snapshot(bool reset) => SnapshotAll(reset);

    // Single-op peek (never resets).
    public PerfStats? Snapshot(string opName)
    {
        if (!_ops.TryGetValue(opName, out var acc)) return null;
        var (samples, dropped) = acc.Flush(reset: false);
        if (samples.Length == 0) return null;
        return BuildStats(opName, samples, dropped);
    }

    private IReadOnlyList<PerfStats> SnapshotAll(bool reset)
    {
        var result = new List<PerfStats>();
        foreach (var (opName, acc) in _ops)
        {
            var (samples, dropped) = acc.Flush(reset);
            if (samples.Length == 0) continue;
            result.Add(BuildStats(opName, samples, dropped));
        }
        return result;
    }

    private static PerfStats BuildStats(string opName, int[] samples, int dropped)
    {
        Array.Sort(samples);
        long total = 0;
        for (var i = 0; i < samples.Length; i++) total += samples[i];
        return new PerfStats(
            opName,
            samples.Length,
            total,
            Percentile(samples, 50),
            Percentile(samples, 95),
            Percentile(samples, 99),
            samples[^1],
            dropped);
    }

    private static int Percentile(int[] sorted, int pct)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(sorted.Length * pct / 100.0) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    private sealed class Accumulator
    {
        private readonly int[] _samples = new int[MaxSamplesPerOp];
        private int _count;
        private int _dropped;
        private readonly object _lock = new();

        public void Add(int micros)
        {
            lock (_lock)
            {
                if (_count < MaxSamplesPerOp) _samples[_count++] = micros;
                else _dropped++;
            }
        }

        public (int[] Samples, int Dropped) Flush(bool reset)
        {
            lock (_lock)
            {
                var copy = new int[_count];
                Array.Copy(_samples, copy, _count);
                var dropped = _dropped;
                if (reset) { _count = 0; _dropped = 0; }
                return (copy, dropped);
            }
        }
    }
}
