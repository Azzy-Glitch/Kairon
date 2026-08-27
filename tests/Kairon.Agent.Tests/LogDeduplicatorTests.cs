using Kairon.Agent.LogTailing;
using Xunit;

namespace Kairon.Agent.Tests;

public class LogDeduplicatorTests
{
    [Fact]
    public void TheFirstOccurrenceOfAMessageIsAlwaysSent()
    {
        var dedup = new LogDeduplicator(TimeSpan.FromSeconds(30));

        Assert.True(dedup.ShouldSend("order failed", DateTime.UtcNow));
    }

    [Fact]
    public void ARepeatWithinTheWindowIsSuppressed()
    {
        var dedup = new LogDeduplicator(TimeSpan.FromSeconds(30));
        var now = DateTime.UtcNow;

        Assert.True(dedup.ShouldSend("order failed", now));
        Assert.False(dedup.ShouldSend("order failed", now.AddSeconds(5)));
        Assert.False(dedup.ShouldSend("order failed", now.AddSeconds(29)));
    }

    [Fact]
    public void ARepeatAfterTheWindowExpiresIsSentAgain()
    {
        var dedup = new LogDeduplicator(TimeSpan.FromSeconds(30));
        var now = DateTime.UtcNow;

        Assert.True(dedup.ShouldSend("order failed", now));
        Assert.True(dedup.ShouldSend("order failed", now.AddSeconds(31)));
    }

    [Fact]
    public void DifferentMessagesAreTrackedIndependently()
    {
        var dedup = new LogDeduplicator(TimeSpan.FromSeconds(30));
        var now = DateTime.UtcNow;

        Assert.True(dedup.ShouldSend("order failed", now));
        Assert.True(dedup.ShouldSend("inventory failed", now));
    }

    [Fact]
    public void PruneRemovesOnlyExpiredEntries()
    {
        var dedup = new LogDeduplicator(TimeSpan.FromSeconds(30));
        var now = DateTime.UtcNow;

        dedup.ShouldSend("old", now);
        dedup.ShouldSend("recent", now.AddSeconds(20));

        dedup.Prune(now.AddSeconds(35));

        // "old" (last seen at t=0, now t=35, window 30s) should have expired and be sendable
        // again; "recent" (last seen at t=20, window still open until t=50) should not.
        Assert.True(dedup.ShouldSend("old", now.AddSeconds(35)));
        Assert.False(dedup.ShouldSend("recent", now.AddSeconds(35)));
    }

    [Fact]
    public void SendingResetsTheWindowFromTheNewOccurrence()
    {
        var dedup = new LogDeduplicator(TimeSpan.FromSeconds(10));
        var now = DateTime.UtcNow;

        Assert.True(dedup.ShouldSend("x", now));
        Assert.True(dedup.ShouldSend("x", now.AddSeconds(11))); // window expired, sent again
        // The window now restarts from t=11, so t=15 (4s later) should still be suppressed.
        Assert.False(dedup.ShouldSend("x", now.AddSeconds(15)));
    }
}
