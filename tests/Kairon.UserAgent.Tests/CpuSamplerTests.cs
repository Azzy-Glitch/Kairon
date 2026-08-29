using Kairon.UserAgent;
using Xunit;

namespace Kairon.UserAgent.Tests;

public sealed class CpuSamplerTests
{
    [Fact]
    public void OneCoreFullyBusyForWholeIntervalIsOneHundredPercent()
    {
        var start = DateTime.UtcNow;
        var percent = CpuSampler.ComputePercent(
            previousCpuTime: TimeSpan.Zero, previousSampledAt: start,
            currentCpuTime: TimeSpan.FromSeconds(1), currentSampledAt: start.AddSeconds(1),
            processorCount: 1);

        Assert.Equal(100.0, percent);
    }

    [Fact]
    public void FourCoreMachineOneCoreFullyBusyIsTwentyFivePercent()
    {
        var start = DateTime.UtcNow;
        var percent = CpuSampler.ComputePercent(
            previousCpuTime: TimeSpan.Zero, previousSampledAt: start,
            currentCpuTime: TimeSpan.FromSeconds(1), currentSampledAt: start.AddSeconds(1),
            processorCount: 4);

        Assert.Equal(25.0, percent);
    }

    [Fact]
    public void ZeroOrNegativeWallClockDeltaNeverFabricatesAPercentage()
    {
        var now = DateTime.UtcNow;

        Assert.Null(CpuSampler.ComputePercent(TimeSpan.Zero, now, TimeSpan.FromSeconds(1), now, 1));
        Assert.Null(CpuSampler.ComputePercent(TimeSpan.Zero, now, TimeSpan.FromSeconds(1), now.AddSeconds(-1), 1));
    }

    [Fact]
    public void ImpossibleCpuTimeIsClampedNotReportedRaw()
    {
        var start = DateTime.UtcNow;
        // A stale/corrupt sample implying >100% on a single core must still clamp, never mislead.
        var percent = CpuSampler.ComputePercent(
            previousCpuTime: TimeSpan.Zero, previousSampledAt: start,
            currentCpuTime: TimeSpan.FromSeconds(5), currentSampledAt: start.AddSeconds(1),
            processorCount: 1);

        Assert.Equal(100.0, percent);
    }

    [Fact]
    public void ZeroProcessorCountNeverDividesByZero()
    {
        var start = DateTime.UtcNow;
        var percent = CpuSampler.ComputePercent(TimeSpan.Zero, start, TimeSpan.FromSeconds(1),
            start.AddSeconds(1), processorCount: 0);

        Assert.Null(percent);
    }
}
