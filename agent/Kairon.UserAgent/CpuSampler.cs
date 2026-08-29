namespace Kairon.UserAgent;

/// <summary>
/// The same processor-time-delta formula agent/Kairon.Agent/ProcessWatch/ProcessWatcher.cs's
/// PollAsync already uses (elapsed CPU time over elapsed wall-clock time, divided by core count),
/// extracted as a pure function so it is unit-testable without spinning real OS processes, and
/// reused by CpuBaselineCache to track many concurrent processes instead of just one.
/// </summary>
public static class CpuSampler
{
    /// <summary>Null when no positive interval actually elapsed - this never fabricates a
    /// percentage from a zero or negative wall-clock delta (e.g. a stale/duplicate sample).</summary>
    public static double? ComputePercent(TimeSpan previousCpuTime, DateTime previousSampledAt,
        TimeSpan currentCpuTime, DateTime currentSampledAt, int processorCount)
    {
        if (processorCount <= 0) return null;

        var elapsedWallMs = (currentSampledAt - previousSampledAt).TotalMilliseconds;
        if (elapsedWallMs <= 0) return null;

        var elapsedCpuMs = (currentCpuTime - previousCpuTime).TotalMilliseconds;
        var percent = elapsedCpuMs / (processorCount * elapsedWallMs) * 100;
        return Math.Round(Math.Clamp(percent, 0, 100), 1);
    }
}
