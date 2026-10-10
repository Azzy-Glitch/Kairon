using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs.Sre;

namespace Kairon.Backend.Services.Evidence;

/// <summary>
/// Deterministic, testable baseline for "will this get worse?" - computed from the incident's own
/// metric samples and recent incident history, before and independently of the AI.
///
/// It only states what the trend shows: each metric's level in the earlier and later half of the
/// evidence window, how persistently the later half breaches its detection threshold, and whether
/// the same problem keeps coming back. It never invents a failure time; the horizon is simply "if
/// the observed trend continues for the next evaluation window". With too few samples the outcome
/// is explicitly <see cref="RiskForecastOutcomes.Inconclusive"/>.
/// </summary>
public static class RiskForecaster
{
    /// <summary>Fewer usable samples than this and no trend is claimed.</summary>
    public const int MinimumSamples = 4;

    public static RiskForecastDto Forecast(
        IReadOnlyList<MetricSampleDto> samples,
        RecurrenceContextDto? recurrence,
        DetectionOptions thresholds)
    {
        var ordered = samples.OrderBy(s => s.Timestamp).ToList();
        var horizonMinutes = Math.Max(5, (int)Math.Ceiling(thresholds.EvaluationWindowSeconds * 2 / 60.0));

        var trends = new List<MetricTrendDto>();
        AddTrend(trends, "errorRate", "Error rate", "%", thresholds.ErrorRateThreshold * 100,
            ordered.Where(s => s.RequestCount > 0).Select(s => (double?)(100.0 * s.ErrorCount / s.RequestCount)).ToList());
        AddTrend(trends, "latency", "Latency", "ms", thresholds.LatencyMsThreshold,
            ordered.Select(s => s.ResponseTimeMs).ToList());
        AddTrend(trends, "cpu", "CPU", "%", thresholds.CpuPercentThreshold,
            ordered.Select(s => s.CpuPercent).ToList());
        AddTrend(trends, "memory", "Memory", "%", thresholds.MemoryPercentThreshold,
            ordered.Select(s => s.MemoryPercent).ToList());

        var recurred = recurrence?.RecurredAfterRestart == true;
        var repeats = recurrence?.IncidentsLast24h ?? 0;

        if (trends.Count == 0)
        {
            return new RiskForecastDto
            {
                Outcome = RiskForecastOutcomes.Inconclusive,
                RiskLevel = recurred ? "high" : "unknown",
                FailureMode = recurred ? "The problem came back after a restart." : string.Empty,
                Evidence = recurred
                    ? new() { RecurrenceEvidence(recurrence!) }
                    : new() { $"Fewer than {MinimumSamples} metric samples in the evidence window - not enough to project a trend." },
                HorizonMinutes = horizonMinutes,
                Confidence = 0,
                ExpectedImpact = string.Empty,
                PreventiveAction = recurred
                    ? "Investigate the application itself - a restart cleared the symptoms only temporarily."
                    : "Keep the app connected so KAIRON can collect enough samples, then re-investigate.",
                Trends = trends
            };
        }

        // The dominant metric is the one furthest over its threshold in the later half.
        var dominant = trends.OrderByDescending(t => t.Persistence).ThenByDescending(t => t.Recent / Math.Max(t.Threshold, 1e-9)).First();
        var breaching = trends.Where(t => t.Persistence >= 0.5).ToList();
        var rising = trends.Where(t => t.Direction == "rising").ToList();

        var level = 0; // 0 low, 1 medium, 2 high, 3 critical
        if (breaching.Count > 0)
        {
            level = dominant.Direction == "falling" ? 1 : 2;
            if (dominant.Recent >= dominant.Threshold * 3 && dominant.Persistence >= 0.75) level = 3;
            if (breaching.Count >= 2 && breaching.Any(t => t.Direction == "rising")) level = 3;
        }
        else if (trends.Any(t => t.Recent >= t.Threshold))
        {
            level = 1;
        }
        else if (rising.Any(t => t.Recent >= t.Threshold * 0.6))
        {
            level = 1;
        }

        if (recurred) level = Math.Min(3, Math.Max(level, 1) + 1);
        else if (repeats >= 2) level = Math.Min(3, level + 1);

        var evidence = trends
            .Where(t => t == dominant || t.Persistence > 0 || t.Direction == "rising")
            .Select(Describe)
            .ToList();
        if (recurred) evidence.Add(RecurrenceEvidence(recurrence!));
        else if (repeats >= 2) evidence.Add($"{repeats} other incident(s) for this service in the last 24 hours.");

        var usable = trends.Max(t => t.Samples);
        // How much the forecast can be trusted: more samples and a consistent signal raise it. It is
        // a property of the data, not a probability of failure.
        var confidence = Math.Round(Math.Min(0.9, 0.35 + 0.05 * Math.Min(usable, 10)) * (breaching.Count > 0 ? Math.Max(0.6, dominant.Persistence) : 0.6), 2);

        return new RiskForecastDto
        {
            Outcome = level >= 2 ? RiskForecastOutcomes.Elevated : RiskForecastOutcomes.Stable,
            RiskLevel = level switch { 3 => "critical", 2 => "high", 1 => "medium", _ => "low" },
            FailureMode = recurred
                ? $"{FailureMode(dominant.Metric, level)} It has already returned after a restart."
                : FailureMode(dominant.Metric, level),
            Evidence = evidence,
            HorizonMinutes = horizonMinutes,
            Confidence = confidence,
            ExpectedImpact = level >= 2 ? Impact(dominant.Metric) : "Little user impact expected if the trend holds.",
            PreventiveAction = recurred
                ? "Investigate the application itself (recent changes, exceptions, dependencies); a restart only cleared the symptoms temporarily."
                : Preventive(dominant.Metric, level),
            Trends = trends
        };
    }

    private static void AddTrend(List<MetricTrendDto> trends, string metric, string label, string unit, double threshold, IReadOnlyList<double?> raw)
    {
        var values = raw.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (values.Count < MinimumSamples) return;

        var half = values.Count / 2;
        var earlier = values.Take(half).Average();
        var later = values.Skip(half).ToList();
        var recent = later.Average();
        var persistence = later.Count(v => v >= threshold) / (double)later.Count;

        // A change counts only when it is both relative (25%) and meaningful against the
        // threshold (10% of it), so noise around zero is never reported as a trend.
        var delta = recent - earlier;
        var meaningful = Math.Abs(delta) >= threshold * 0.1;
        var direction = !meaningful ? "steady"
            : delta > 0 && recent >= earlier * 1.25 ? "rising"
            : delta < 0 && recent <= earlier * 0.8 ? "falling"
            : "steady";

        trends.Add(new MetricTrendDto
        {
            Metric = metric,
            Label = label,
            Unit = unit,
            Earlier = Math.Round(earlier, 1),
            Recent = Math.Round(recent, 1),
            Threshold = Math.Round(threshold, 1),
            Direction = direction,
            Persistence = Math.Round(persistence, 2),
            Samples = values.Count
        });
    }

    private static string Describe(MetricTrendDto t) =>
        $"{t.Label} {t.Direction}: {t.Earlier:0.#}{t.Unit} earlier, {t.Recent:0.#}{t.Unit} recently " +
        $"(threshold {t.Threshold:0.#}{t.Unit}; over it in {t.Persistence:P0} of recent samples).";

    private static string RecurrenceEvidence(RecurrenceContextDto r) =>
        r.MinutesSinceRestart is { } minutes
            ? $"A previous incident for this service was remediated by {r.PreviousRemediation ?? "a restart"} {minutes:0} minute(s) ago, and the problem returned."
            : "The problem returned after a previous restart.";

    private static string FailureMode(string metric, int level) => (metric, level >= 2) switch
    {
        ("errorRate", true) => "Requests keep failing with server errors.",
        ("latency", true) => "Requests keep slowing down and may start timing out.",
        ("cpu", true) => "CPU stays saturated, slowing every request on this process.",
        ("memory", true) => "Memory keeps climbing toward exhaustion, risking a crash.",
        ("errorRate", false) => "Errors are present but not clearly getting worse.",
        ("latency", false) => "Latency is elevated but not clearly getting worse.",
        ("cpu", false) => "CPU is elevated but not clearly getting worse.",
        _ => "Memory is elevated but not clearly getting worse."
    };

    private static string Impact(string metric) => metric switch
    {
        "errorRate" => "Users keep seeing failed requests until the cause is removed.",
        "latency" => "Slow responses and possible timeouts for users and callers of this service.",
        "cpu" => "All requests on this process slow down; dependent services may back up.",
        _ => "The process may be killed or crash when memory runs out."
    };

    private static string Preventive(string metric, int level) => (metric, level >= 2) switch
    {
        ("errorRate", true) => "Mitigate now (an approved restart if the failure is in-process state), then fix the failing code path or dependency.",
        ("latency", true) => "Check slow dependencies and capacity; a restart helps only if the slowdown is caused inside the process.",
        ("cpu", true) => "Find the CPU-heavy work (background jobs, hot loops); a restart relieves it only temporarily.",
        ("memory", true) => "Look for a leak or unbounded cache; a restart buys time but the growth will repeat.",
        _ => "Keep monitoring; no action is needed unless the trend starts rising."
    };
}
