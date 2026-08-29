using Kairon.UserAgent;
using Xunit;

namespace Kairon.UserAgent.Tests;

public sealed class CpuBaselineCacheTests
{
    [Fact]
    public void FirstSightingOfAProcessReportsNoPercentYet()
    {
        var cache = new CpuBaselineCache();
        var start = DateTime.UtcNow;

        var result = cache.Update([(42, start, TimeSpan.Zero, DateTime.UtcNow)], processorCount: 1);

        Assert.Empty(result);
    }

    [Fact]
    public void SecondSightingComputesARealDelta()
    {
        var cache = new CpuBaselineCache();
        var processStart = DateTime.UtcNow;
        var t1 = DateTime.UtcNow;
        cache.Update([(42, processStart, TimeSpan.Zero, t1)], processorCount: 1);

        var t2 = t1.AddSeconds(1);
        var result = cache.Update([(42, processStart, TimeSpan.FromMilliseconds(500), t2)], processorCount: 1);

        Assert.True(result.TryGetValue((42, processStart), out var percent));
        Assert.Equal(50.0, percent);
    }

    [Fact]
    public void ProcessTerminationDropsItsBaselineWithoutThrowing()
    {
        var cache = new CpuBaselineCache();
        var processStart = DateTime.UtcNow;
        cache.Update([(42, processStart, TimeSpan.Zero, DateTime.UtcNow)], processorCount: 1);

        // Process 42 is gone from this poll (it exited) - nothing in `current` refers to it.
        var result = cache.Update([], processorCount: 1);
        Assert.Empty(result);

        // A brand new process reusing PID 42 must get its own fresh baseline, not the old one.
        var newStart = DateTime.UtcNow;
        var afterReuse = cache.Update([(42, newStart, TimeSpan.FromSeconds(10), DateTime.UtcNow)], processorCount: 1);
        Assert.Empty(afterReuse);
    }

    [Fact]
    public void DifferentStartTimeForSamePidIsTreatedAsADifferentProcess()
    {
        var cache = new CpuBaselineCache();
        var t1 = DateTime.UtcNow;
        cache.Update([(42, t1, TimeSpan.Zero, t1)], processorCount: 1);

        // Same PID, but a different StartedAt - a new process that happens to reuse the PID -
        // must not be conflated with the previous one's baseline.
        var newStart = t1.AddMinutes(1);
        var result = cache.Update([(42, newStart, TimeSpan.FromSeconds(5), t1.AddMinutes(2))], processorCount: 1);

        Assert.Empty(result);
    }
}
