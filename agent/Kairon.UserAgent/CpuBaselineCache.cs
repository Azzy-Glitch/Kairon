namespace Kairon.UserAgent;

/// <summary>
/// Tracks a CPU-time baseline per (process id, start time) identity across heartbeat ticks, so
/// CpuSampler can compute a real delta-based percentage for many concurrent processes at once -
/// generalizing the single-process baseline fields agent/Kairon.Agent/ProcessWatch/
/// ProcessWatcher.cs keeps as plain fields. Keyed by (Pid, StartedAt) rather than just Pid so a
/// new process that happens to reuse an old, exited process's PID is never confused with it and
/// never inherits a stale baseline.
/// </summary>
public sealed class CpuBaselineCache
{
    private Dictionary<(int Pid, DateTime StartedAt), (TimeSpan CpuTime, DateTime SampledAt)> _baselines = new();

    /// <summary>
    /// Returns a CPU percent only for processes that already had a baseline from the previous
    /// call - a process seen for the first time gets one silent baseline-only tick (same as
    /// ProcessWatcher today) and is simply absent from the returned map until its second sample.
    /// Baselines for processes not present in <paramref name="current"/> are dropped, so a process
    /// that exits is forgotten rather than leaking in this cache forever.
    /// </summary>
    public IReadOnlyDictionary<(int Pid, DateTime StartedAt), double> Update(
        IEnumerable<(int Pid, DateTime StartedAt, TimeSpan CpuTime, DateTime SampledAt)> current,
        int processorCount)
    {
        var next = new Dictionary<(int, DateTime), (TimeSpan, DateTime)>();
        var percents = new Dictionary<(int, DateTime), double>();

        foreach (var sample in current)
        {
            var key = (sample.Pid, sample.StartedAt);
            if (_baselines.TryGetValue(key, out var baseline))
            {
                var percent = CpuSampler.ComputePercent(baseline.CpuTime, baseline.SampledAt,
                    sample.CpuTime, sample.SampledAt, processorCount);
                if (percent.HasValue) percents[key] = percent.Value;
            }

            next[key] = (sample.CpuTime, sample.SampledAt);
        }

        _baselines = next;
        return percents;
    }
}
