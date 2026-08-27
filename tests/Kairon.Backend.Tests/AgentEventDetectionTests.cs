using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Detection;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Detection rules over KAIRON Agent-sourced signals (docs/OBSERVABILITY_MIGRATION.md):
/// log pattern matches and process lifecycle/resource events, folded into the same
/// DetectionSignal/DetectionContext pipeline the metric/HTTP rules already use.
/// </summary>
public class AgentEventDetectionTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly DateTime _now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private AgentEvent Event(string eventType, string severity, string message, string source = "test.log", DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = _h.ProjectId,
        Timestamp = at ?? _now.AddSeconds(-5),
        EventType = eventType,
        Environment = _h.Environment,
        Service = _h.Service,
        Severity = severity,
        Message = message,
        Source = source
    };

    // --- LogPatternMatchRule ---

    [Fact]
    public void LogPatternRuleStaysQuietBelowTheMinimumCount()
    {
        var events = new[] { Event("LogPatternMatch", "Error", "Order processing failed") };
        Assert.Null(new LogPatternMatchRule().Evaluate(_h.Context(_now, agentEvents: events)));
    }

    [Fact]
    public void LogPatternRuleFiresOnceTheMinimumCountIsReached()
    {
        var events = new[]
        {
            Event("LogPatternMatch", "Error", "Order processing failed", at: _now.AddSeconds(-30)),
            Event("LogPatternMatch", "Error", "Order processing failed", at: _now.AddSeconds(-5))
        };

        var signal = new LogPatternMatchRule().Evaluate(_h.Context(_now, agentEvents: events));

        Assert.NotNull(signal);
        Assert.Equal("logPattern", signal!.MetricName);
        Assert.Equal(2, signal.Observed);
        Assert.Contains("Order processing failed", signal.Symptom);
    }

    [Fact]
    public void LogPatternRuleSeverityFollowsTheWorstReportedOccurrence()
    {
        var events = new[]
        {
            Event("LogPatternMatch", "Warning", "Minor issue"),
            Event("LogPatternMatch", "Critical", "OutOfMemoryException")
        };

        var signal = new LogPatternMatchRule().Evaluate(_h.Context(_now, agentEvents: events));

        Assert.NotNull(signal);
        Assert.Equal(IncidentSeverity.Critical, signal!.Severity);
    }

    [Fact]
    public void LogPatternRuleIgnoresOtherEventTypes()
    {
        var events = new[]
        {
            Event("ProcessStarted", "Info", "Process started"),
            Event("ProcessCrash", "Critical", "Process crashed")
        };

        Assert.Null(new LogPatternMatchRule().Evaluate(_h.Context(_now, agentEvents: events)));
    }

    // --- ProcessCrashRule ---

    [Fact]
    public void ProcessCrashRuleFiresOnASingleOccurrence()
    {
        var events = new[] { Event("ProcessCrash", "Critical", "Process 'Kairon.DemoApp' is no longer running", source: "Kairon.DemoApp") };

        var signal = new ProcessCrashRule().Evaluate(_h.Context(_now, agentEvents: events));

        Assert.NotNull(signal);
        Assert.Equal("processCrash", signal!.MetricName);
        Assert.Equal(IncidentSeverity.Critical, signal.Severity);
        Assert.Equal("Kairon.DemoApp", signal.Component);
    }

    [Fact]
    public void ProcessCrashRuleStaysQuietWithNoCrashEvent()
    {
        var events = new[] { Event("ProcessStarted", "Info", "Process started") };
        Assert.Null(new ProcessCrashRule().Evaluate(_h.Context(_now, agentEvents: events)));
    }

    // --- ProcessHighResourceRule ---

    [Fact]
    public void ProcessHighResourceRuleFiresOnASingleOccurrence()
    {
        var events = new[] { Event("ProcessHighResource", "Warning", "CPU at 92%") };

        var signal = new ProcessHighResourceRule().Evaluate(_h.Context(_now, agentEvents: events));

        Assert.NotNull(signal);
        Assert.Equal("processHighResource", signal!.MetricName);
        Assert.Equal(IncidentSeverity.Medium, signal.Severity);
    }

    [Fact]
    public void ProcessHighResourceRuleStaysQuietWithNoEvent()
    {
        Assert.Null(new ProcessHighResourceRule().Evaluate(_h.Context(_now)));
    }

    // --- Cross-rule: a broken rule must not blind the others (mirrors DetectionEngine's own try/catch) ---

    [Fact]
    public void AllThreeAgentRulesCanFireTogetherFromTheSameContext()
    {
        var events = new[]
        {
            Event("LogPatternMatch", "Error", "Order processing failed", at: _now.AddSeconds(-30)),
            Event("LogPatternMatch", "Error", "Order processing failed", at: _now.AddSeconds(-5)),
            Event("ProcessCrash", "Critical", "Process crashed"),
            Event("ProcessHighResource", "Warning", "CPU at 92%")
        };

        var context = _h.Context(_now, agentEvents: events);

        Assert.NotNull(new LogPatternMatchRule().Evaluate(context));
        Assert.NotNull(new ProcessCrashRule().Evaluate(context));
        Assert.NotNull(new ProcessHighResourceRule().Evaluate(context));
    }
}
