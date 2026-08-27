using Kairon.Agent.LogTailing;
using Xunit;

namespace Kairon.Agent.Tests;

public class LogPatternMatcherTests
{
    [Fact]
    public void InfoLevelEntriesAreNeverReported()
    {
        var result = LogPatternMatcher.Match(new LogEntry { Level = "INF", Message = "Request completed in 42ms" });

        Assert.Null(result);
    }

    [Fact]
    public void WarningLevelWithNoRecognizedPatternIsNotReported()
    {
        var result = LogPatternMatcher.Match(new LogEntry { Level = "WRN", Message = "Cache miss for key X" });

        Assert.Null(result);
    }

    [Fact]
    public void ErrorLevelIsReportedAsWarningWhenItDoesNotLookLikeAnException()
    {
        var result = LogPatternMatcher.Match(new LogEntry { Level = "ERR", Message = "Order processing failed: timeout" });

        Assert.NotNull(result);
        Assert.Equal("LogPatternMatch", result!.EventType);
        Assert.Equal("Warning", result.Severity);
    }

    [Fact]
    public void ErrorLevelWithAStackTraceIsReportedAsError()
    {
        var result = LogPatternMatcher.Match(new LogEntry
        {
            Level = "ERR",
            Message = "Order processing failed: timeout\n   at Kairon.DemoApp.DemoScenario.ProcessOrder()"
        });

        Assert.NotNull(result);
        Assert.Equal("Error", result!.Severity);
    }

    [Fact]
    public void ErrorLevelMentioningExceptionIsReportedAsError()
    {
        var result = LogPatternMatcher.Match(new LogEntry { Level = "ERR", Message = "Unhandled InvalidOperationException in handler" });

        Assert.NotNull(result);
        Assert.Equal("Error", result!.Severity);
    }

    [Fact]
    public void FatalLevelIsAlwaysCritical()
    {
        var result = LogPatternMatcher.Match(new LogEntry { Level = "FTL", Message = "Host terminated unexpectedly" });

        Assert.NotNull(result);
        Assert.Equal("Critical", result!.Severity);
    }

    [Theory]
    [InlineData("System.OutOfMemoryException: Insufficient memory")]
    [InlineData("Process ran out of memory while processing batch")]
    [InlineData("OOM killer invoked for process 1234")]
    public void OutOfMemoryMarkersAreAlwaysCriticalRegardlessOfLevel(string message)
    {
        var result = LogPatternMatcher.Match(new LogEntry { Level = "WRN", Message = message });

        Assert.NotNull(result);
        Assert.Equal("Critical", result!.Severity);
    }

    [Fact]
    public void TheMatchedMessageIsPreservedVerbatim()
    {
        const string message = "Order processing failed: timeout\n   at Frame()";
        var result = LogPatternMatcher.Match(new LogEntry { Level = "ERR", Message = message });

        Assert.Equal(message, result!.Message);
    }
}
