using Kairon.Agent.LogTailing;
using Xunit;

namespace Kairon.Agent.Tests;

/// <summary>
/// LogEventExtractor against real Serilog output shape (confirmed against
/// backend/logs/*.txt, not guessed): "{yyyy-MM-dd HH:mm:ss.fff} {+HH:mm} [{LVL}] {message}",
/// with continuation lines (stack traces) carrying no leading timestamp.
/// </summary>
public class LogEventExtractorTests
{
    private const string Ts = "2026-08-27 12:12:49.134 +05:00";

    [Fact]
    public void ASingleLineEntryIsExtractedWhenTheTextEndsWithANewline()
    {
        var (finalized, pending) = LogEventExtractor.Extract($"{Ts} [INF] Request completed\n", pending: null);

        var entry = Assert.Single(finalized);
        Assert.Equal("INF", entry.Level);
        Assert.Equal("Request completed", entry.Message);
        Assert.Null(pending);
    }

    [Fact]
    public void MultipleEntriesInOneBatchAreAllExtracted()
    {
        var text = $"{Ts} [INF] First\n{Ts} [WRN] Second\n{Ts} [ERR] Third\n";

        var (finalized, pending) = LogEventExtractor.Extract(text, pending: null);

        Assert.Equal(3, finalized.Count);
        Assert.Equal(new[] { "First", "Second", "Third" }, finalized.Select(e => e.Message));
        Assert.Null(pending);
    }

    [Fact]
    public void ContinuationLinesAreGroupedIntoThePrecedingEntry()
    {
        var text = $"{Ts} [ERR] Order processing failed: timeout\n" +
                    "   at Kairon.DemoApp.DemoScenario.ProcessOrder()\n" +
                    "   at Kairon.DemoApp.Program.<>c.<<Main>$>b__0_6(DemoScenario scenario)\n" +
                    $"{Ts} [INF] Next request\n";

        var (finalized, _) = LogEventExtractor.Extract(text, pending: null);

        Assert.Equal(2, finalized.Count);
        Assert.Contains("Order processing failed: timeout", finalized[0].Message);
        Assert.Contains("at Kairon.DemoApp.DemoScenario.ProcessOrder()", finalized[0].Message);
        Assert.Contains("at Kairon.DemoApp.Program", finalized[0].Message);
        Assert.Equal("Next request", finalized[1].Message);
    }

    [Fact]
    public void AnEntryNotEndingInANewlineIsHeldBackAsPendingNotFinalized()
    {
        // The writer may still be mid-write when the Agent polls - the last entry in the batch
        // should not be finalized (and sent) until it's known to be complete.
        var text = $"{Ts} [ERR] Something failed\n   at SomeMethod()";

        var (finalized, pending) = LogEventExtractor.Extract(text, pending: null);

        Assert.Empty(finalized);
        Assert.NotNull(pending);
        Assert.Contains("Something failed", pending!.Message);
        Assert.Contains("at SomeMethod()", pending.Message);
    }

    [Fact]
    public void APendingEntryFromAPreviousPollContinuesAccumulatingContinuationLines()
    {
        var (_, pendingAfterFirstPoll) = LogEventExtractor.Extract(
            $"{Ts} [ERR] Something failed\n   at FirstFrame()", pending: null);

        // Next poll brings more of the same stack trace, then a clean new entry.
        var secondPollText = "   at SecondFrame()\n" + $"{Ts} [INF] Recovered\n";

        var (finalized, pendingAfterSecondPoll) = LogEventExtractor.Extract(secondPollText, pendingAfterFirstPoll);

        // The entry that was pending completes (with both stack frames merged in) once the
        // second poll's clean new entry-start line arrives; that new entry ("Recovered") is
        // itself complete too, since the batch ended with a newline.
        Assert.Equal(2, finalized.Count);
        var completed = finalized[0];
        Assert.Contains("Something failed", completed.Message);
        Assert.Contains("at FirstFrame()", completed.Message);
        Assert.Contains("at SecondFrame()", completed.Message);
        Assert.Equal("Recovered", finalized[1].Message);
        Assert.Null(pendingAfterSecondPoll);
    }

    [Fact]
    public void EmptyTextProducesNoEntriesAndPreservesAnyPending()
    {
        var (_, pendingBefore) = LogEventExtractor.Extract($"{Ts} [ERR] X\n   at Y()", pending: null);

        var (finalized, pendingAfter) = LogEventExtractor.Extract(string.Empty, pendingBefore);

        Assert.Empty(finalized);
        Assert.Same(pendingBefore, pendingAfter);
    }

    [Fact]
    public void BlankContinuationLinesAreIgnoredNotTreatedAsContent()
    {
        var text = $"{Ts} [ERR] Failure\n\n   at Frame()\n\n{Ts} [INF] Next\n";

        var (finalized, _) = LogEventExtractor.Extract(text, pending: null);

        Assert.Equal(2, finalized.Count);
        Assert.DoesNotContain("\n\n", finalized[0].Message);
    }
}
