using Kairon.Backend.Configuration;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;

namespace Kairon.Backend.Services.Detection;

/// <summary>
/// Everything a detection rule is allowed to look at. Rules are pure functions of this context,
/// which is what makes them unit-testable without a database.
/// </summary>
public class DetectionContext
{
    public required DetectionOptions Options { get; init; }

    public Guid ProjectId { get; init; }
    public string Application { get; init; } = "Unknown";
    public string Service { get; init; } = "Unknown";
    public string Environment { get; init; } = "Development";

    /// <summary>Metric samples inside the evaluation window, oldest first.</summary>
    public IReadOnlyList<Metric> Metrics { get; init; } = Array.Empty<Metric>();

    /// <summary>Telemetry rows inside the evaluation window, oldest first.</summary>
    public IReadOnlyList<Incident> Telemetry { get; init; } = Array.Empty<Incident>();

    /// <summary>KAIRON Agent events (log pattern matches, process lifecycle/resource events)
    /// inside the evaluation window, oldest first (docs/OBSERVABILITY_MIGRATION.md).</summary>
    public IReadOnlyList<AgentEvent> AgentEvents { get; init; } = Array.Empty<AgentEvent>();

    /// <summary>Evaluation instant. Injected rather than read from the clock so tests are deterministic.</summary>
    public DateTime Now { get; init; } = DateTime.UtcNow;

    public DateTime WindowStart => Now.AddSeconds(-Options.EvaluationWindowSeconds);

    /// <summary>
    /// The period a per-minute rate should be divided by.
    ///
    /// This is the span the samples actually cover, not the nominal window length. Using the
    /// nominal window understates a rate whenever the data is younger than the window - twenty
    /// seconds of samples divided by a two-minute window reads six times too low, which is exactly
    /// when a retry storm is ramping and most worth catching. The observed span is clamped to the
    /// window so a single stale sample cannot inflate a rate either.
    /// </summary>
    public double WindowMinutes
    {
        get
        {
            var nominal = Math.Max(Options.EvaluationWindowSeconds / 60.0, 1.0 / 60.0);

            if (Metrics.Count < 2)
                return Math.Min(nominal, 1.0 / 6.0);

            var spanSeconds = (Metrics[^1].Timestamp - Metrics[0].Timestamp).TotalSeconds;

            // One sampling interval is added, because N samples cover N intervals of activity
            // rather than the N-1 gaps between their timestamps.
            var interval = spanSeconds / Math.Max(1, Metrics.Count - 1);
            var observed = (spanSeconds + interval) / 60.0;

            return Math.Clamp(observed, 1.0 / 60.0, nominal);
        }
    }

    public bool HasEnoughSamples => Metrics.Count >= Options.MinimumSamples;

    /// <summary>
    /// True when a metric has been over its threshold for at least the sustained-breach period.
    /// A single spiky sample is not an incident (PRD section 7: "CPU &gt; threshold AND duration &gt;
    /// configured interval").
    /// </summary>
    public bool IsSustained(Func<Metric, double?> selector, double threshold)
    {
        var breaching = Metrics
            .Where(m => selector(m) is { } v && v > threshold)
            .ToList();

        if (breaching.Count < Options.MinimumSamples)
            return false;

        var span = breaching[^1].Timestamp - breaching[0].Timestamp;
        if (span.TotalSeconds >= Options.SustainedBreachSeconds)
            return true;

        // Sampling may be coarser than the sustained window, so "every sample we have is
        // breaching" also counts - but only once we have actually been watching for at least the
        // sustained period. Without that second condition the leading edge of a ramp looks
        // identical to a sustained breach: two samples three seconds apart would satisfy a
        // six-second requirement, and every rule would fire the instant a metric started climbing.
        var observedSpan = Metrics[^1].Timestamp - Metrics[0].Timestamp;

        return breaching.Count == Metrics.Count
               && Metrics.Count >= Options.MinimumSamples
               && observedSpan.TotalSeconds >= Options.SustainedBreachSeconds;
    }

    public double? Average(Func<Metric, double?> selector)
    {
        var values = Metrics.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? null : values.Average();
    }

    public double? Peak(Func<Metric, double?> selector)
    {
        var values = Metrics.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? null : values.Max();
    }

    /// <summary>Creates a signal pre-filled with this context's identity fields.</summary>
    public DetectionSignal NewSignal(DetectionRuleKind kind, string ruleId) => new()
    {
        Rule = kind,
        RuleId = ruleId,
        ProjectId = ProjectId,
        Application = Application,
        Service = Service,
        Environment = Environment,
        Component = Service,
        DetectedAt = Now
    };

    /// <summary>
    /// Severity from how badly a threshold is breached. Deterministic, so the same numbers always
    /// produce the same severity regardless of what the AI later concludes.
    /// </summary>
    public IncidentSeverity SeverityFor(double observed, double threshold)
    {
        if (threshold <= 0)
            return IncidentSeverity.Medium;

        var ratio = observed / threshold;
        if (ratio >= Options.CriticalSeverityRatio) return IncidentSeverity.Critical;
        if (ratio >= Options.HighSeverityRatio) return IncidentSeverity.High;
        return IncidentSeverity.Medium;
    }
}
