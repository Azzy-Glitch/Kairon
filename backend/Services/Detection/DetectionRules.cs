using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;

namespace Kairon.Backend.Services.Detection;

/// <summary>
/// A deterministic detection rule (PRD section 7). Rules run before any AI reasoning and are the
/// only thing that can create an incident - the AI never gets to decide that something is wrong.
/// </summary>
public interface IDetectionRule
{
    DetectionRuleKind Kind { get; }
    string RuleId { get; }

    /// <summary>Returns a signal when the rule fires, or null when the system looks healthy.</summary>
    DetectionSignal? Evaluate(DetectionContext context);
}

public class CpuThresholdRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.CpuThreshold;
    public string RuleId => "cpu-threshold";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        if (!ctx.HasEnoughSamples) return null;

        var threshold = ctx.Options.CpuPercentThreshold;
        if (!ctx.IsSustained(m => m.CpuPercent, threshold)) return null;

        var peak = ctx.Peak(m => m.CpuPercent) ?? 0;
        var avg = ctx.Average(m => m.CpuPercent) ?? 0;

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "cpu";
        signal.Observed = Math.Round(peak, 1);
        signal.Threshold = threshold;
        signal.Unit = "%";
        signal.Severity = ctx.SeverityFor(peak, threshold);
        signal.Symptom = $"CPU sustained at {Math.Round(avg, 1)}% (peak {Math.Round(peak, 1)}%), threshold {threshold}%";
        return signal;
    }
}

public class MemoryThresholdRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.MemoryThreshold;
    public string RuleId => "memory-threshold";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        if (!ctx.HasEnoughSamples) return null;

        var threshold = ctx.Options.MemoryPercentThreshold;
        if (!ctx.IsSustained(m => m.MemoryPercent, threshold)) return null;

        var peak = ctx.Peak(m => m.MemoryPercent) ?? 0;
        var avg = ctx.Average(m => m.MemoryPercent) ?? 0;

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "memory";
        signal.Observed = Math.Round(peak, 1);
        signal.Threshold = threshold;
        signal.Unit = "%";
        signal.Severity = ctx.SeverityFor(peak, threshold);
        signal.Symptom = $"Memory sustained at {Math.Round(avg, 1)}% (peak {Math.Round(peak, 1)}%), threshold {threshold}%";
        return signal;
    }
}

public class LatencyThresholdRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.LatencyThreshold;
    public string RuleId => "latency-threshold";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var threshold = ctx.Options.LatencyMsThreshold;

        // Latency can be evidenced either by metric samples or by slow telemetry rows, so the rule
        // considers both rather than going blind when only one source is present.
        var metricSustained = ctx.HasEnoughSamples && ctx.IsSustained(m => m.ResponseTimeMs, threshold);

        var slowCalls = ctx.Telemetry.Where(t => t.DurationMs > threshold).ToList();
        var telemetrySustained = slowCalls.Count >= ctx.Options.MinimumSamples;

        if (!metricSustained && !telemetrySustained) return null;

        var metricPeak = ctx.Peak(m => m.ResponseTimeMs) ?? 0;
        var telemetryPeak = slowCalls.Count > 0 ? slowCalls.Max(t => (double)t.DurationMs) : 0;
        var peak = Math.Max(metricPeak, telemetryPeak);

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "latency";
        signal.Observed = Math.Round(peak, 0);
        signal.Threshold = threshold;
        signal.Unit = "ms";
        signal.Severity = ctx.SeverityFor(peak, threshold);
        signal.Symptom = $"Response time peaked at {Math.Round(peak, 0)}ms, threshold {threshold}ms";
        signal.Endpoint = slowCalls.LastOrDefault()?.Endpoint ?? string.Empty;
        signal.TelemetryReferences = slowCalls.TakeLast(20).Select(t => t.Id).ToList();
        return signal;
    }
}

public class ErrorRateRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.ErrorRateThreshold;
    public string RuleId => "error-rate-threshold";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var threshold = ctx.Options.ErrorRateThreshold;

        // Prefer counter metrics; fall back to the telemetry rows when no counters were reported.
        long requests = ctx.Metrics.Sum(m => m.RequestCount);
        long errors = ctx.Metrics.Sum(m => m.ErrorCount);

        if (requests == 0)
        {
            requests = ctx.Telemetry.Count;
            errors = ctx.Telemetry.Count(t => t.StatusCode >= 500 || !string.IsNullOrEmpty(t.ErrorMessage));
        }

        if (requests < ctx.Options.MinimumSamples || errors == 0) return null;

        var rate = (double)errors / requests;
        if (rate <= threshold) return null;

        var failing = ctx.Telemetry
            .Where(t => t.StatusCode >= 500 || !string.IsNullOrEmpty(t.ErrorMessage))
            .ToList();

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "errorRate";
        signal.Observed = Math.Round(rate * 100, 1);
        signal.Threshold = Math.Round(threshold * 100, 1);
        signal.Unit = "%";
        signal.Severity = ctx.SeverityFor(rate, threshold);
        signal.Symptom = $"Error rate {Math.Round(rate * 100, 1)}% ({errors}/{requests}), threshold {Math.Round(threshold * 100, 1)}%";
        signal.Endpoint = failing.LastOrDefault()?.Endpoint ?? string.Empty;
        signal.TelemetryReferences = failing.TakeLast(20).Select(t => t.Id).ToList();
        return signal;
    }
}

public class RetryStormRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.RetryStorm;
    public string RuleId => "retry-storm";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var withRetries = ctx.Metrics.Where(m => m.RetryCount.HasValue).ToList();
        if (withRetries.Count == 0) return null;

        var totalRetries = withRetries.Sum(m => m.RetryCount ?? 0);
        if (totalRetries == 0) return null;

        var perMinute = totalRetries / ctx.WindowMinutes;
        var threshold = ctx.Options.RetryStormPerMinute;
        if (perMinute <= threshold) return null;

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "retries";
        signal.Observed = Math.Round(perMinute, 1);
        signal.Threshold = threshold;
        signal.Unit = "/min";
        signal.Severity = ctx.SeverityFor(perMinute, threshold);
        signal.Symptom = $"Retry rate {Math.Round(perMinute, 1)}/min ({totalRetries} retries), threshold {threshold}/min";
        return signal;
    }
}

public class RequestBurstRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.RequestBurst;
    public string RuleId => "request-burst";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var totalRequests = ctx.Metrics.Sum(m => m.RequestCount);
        if (totalRequests == 0) return null;

        var perMinute = totalRequests / ctx.WindowMinutes;
        var threshold = ctx.Options.RequestBurstPerMinute;
        if (perMinute <= threshold) return null;

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "requests";
        signal.Observed = Math.Round(perMinute, 1);
        signal.Threshold = threshold;
        signal.Unit = "/min";
        signal.Severity = ctx.SeverityFor(perMinute, threshold);
        signal.Symptom = $"Request burst {Math.Round(perMinute, 1)}/min, threshold {threshold}/min";
        return signal;
    }
}

/// <summary>
/// Sudden metric deviation (PRD section 7). Compares the newest sample against the mean and
/// standard deviation of the earlier samples in the window, so a step change is caught even when
/// the absolute value has not yet crossed a static threshold.
/// </summary>
public class MetricDeviationRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.MetricDeviation;
    public string RuleId => "metric-deviation";

    private static readonly (string Name, string Unit, Func<Metric, double?> Selector)[] Tracked =
    {
        ("cpu", "%", m => m.CpuPercent),
        ("memory", "%", m => m.MemoryPercent),
        ("latency", "ms", m => m.ResponseTimeMs),
        ("queue", "", m => m.QueueDepth)
    };

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        // Needs a baseline plus a current sample; four is the smallest window where a standard
        // deviation is worth anything at all.
        if (ctx.Metrics.Count < 4) return null;

        foreach (var (name, unit, selector) in Tracked)
        {
            var series = ctx.Metrics
                .Select(selector)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (series.Count < 4) continue;

            var current = series[^1];
            var baseline = series.Take(series.Count - 1).ToList();
            var mean = baseline.Average();
            var variance = baseline.Sum(v => Math.Pow(v - mean, 2)) / baseline.Count;
            var stdDev = Math.Sqrt(variance);

            // A flat baseline gives stdDev 0 and would divide by zero; require real movement.
            if (stdDev < 0.0001) continue;

            var sigma = (current - mean) / stdDev;
            if (sigma < ctx.Options.DeviationSigma) continue;

            var signal = ctx.NewSignal(Kind, $"{RuleId}:{name}");
            signal.MetricName = name;
            signal.Observed = Math.Round(current, 1);
            signal.Threshold = Math.Round(mean, 1);
            signal.Unit = unit;
            signal.Severity = sigma >= ctx.Options.DeviationSigma * 2
                ? IncidentSeverity.High
                : IncidentSeverity.Medium;
            signal.Symptom =
                $"Sudden {name} deviation: {Math.Round(current, 1)}{unit} vs baseline {Math.Round(mean, 1)}{unit} " +
                $"({Math.Round(sigma, 1)} sigma)";
            return signal;
        }

        return null;
    }
}

/// <summary>Repeated identical errors on one endpoint (PRD section 7).</summary>
public class RepeatedErrorsRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.RepeatedErrors;
    public string RuleId => "repeated-errors";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var errors = ctx.Telemetry
            .Where(t => !string.IsNullOrWhiteSpace(t.ErrorMessage) || t.StatusCode >= 500)
            .ToList();

        if (errors.Count < ctx.Options.RepeatedErrorCount) return null;

        var group = errors
            .GroupBy(t => new { t.Endpoint, Type = t.ErrorType ?? t.StatusCode.ToString() })
            .Select(g => new { g.Key, Count = g.Count(), Items = g.ToList() })
            .OrderByDescending(g => g.Count)
            .First();

        if (group.Count < ctx.Options.RepeatedErrorCount) return null;

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "errors";
        signal.Observed = group.Count;
        signal.Threshold = ctx.Options.RepeatedErrorCount;
        signal.Unit = " occurrences";
        signal.Severity = ctx.SeverityFor(group.Count, ctx.Options.RepeatedErrorCount);
        signal.Endpoint = group.Key.Endpoint;
        signal.Component = string.IsNullOrEmpty(group.Key.Endpoint) ? ctx.Service : group.Key.Endpoint;
        signal.Symptom =
            $"{group.Count} repeated errors on {group.Key.Endpoint} ({group.Key.Type}), " +
            $"threshold {ctx.Options.RepeatedErrorCount}";
        signal.TelemetryReferences = group.Items.TakeLast(20).Select(t => t.Id).ToList();
        return signal;
    }
}

/// <summary>
/// Repeated log entries the KAIRON Agent matched against its pattern set - an unhandled
/// exception, an OOM marker, or a logged error with a stack trace
/// (docs/OBSERVABILITY_MIGRATION.md). The Agent already deduplicates identical lines on its own
/// timer, so more than one distinct reported occurrence reaching the backend is real, repeated
/// evidence, not one noisy line - the same "one spike is not an incident" reasoning the
/// threshold rules apply to metrics, applied to log-sourced signals instead.
/// </summary>
public class LogPatternMatchRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.LogPatternMatch;
    public string RuleId => "log-pattern-match";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var matches = ctx.AgentEvents.Where(e => e.EventType == "LogPatternMatch").ToList();
        if (matches.Count < ctx.Options.LogPatternMatchMinCount) return null;

        var worst = matches.OrderByDescending(e => SeverityRank(e.Severity)).First();

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "logPattern";
        signal.Observed = matches.Count;
        signal.Threshold = ctx.Options.LogPatternMatchMinCount;
        signal.Unit = " occurrences";
        signal.Severity = ToIncidentSeverity(worst.Severity);
        signal.Component = worst.Source;
        signal.Symptom = $"{matches.Count} matched log pattern(s), most recent: {Truncate(worst.Message, 200)}";
        return signal;
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 3,
        "Error" => 2,
        "Warning" => 1,
        _ => 0
    };

    private static IncidentSeverity ToIncidentSeverity(string agentSeverity) => agentSeverity switch
    {
        "Critical" => IncidentSeverity.Critical,
        "Error" => IncidentSeverity.High,
        "Warning" => IncidentSeverity.Medium,
        _ => IncidentSeverity.Low
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

/// <summary>
/// A process the KAIRON Agent was watching stopped unexpectedly. Fires on the first occurrence -
/// unlike a single log line, a crash is unambiguously severe evidence on its own.
/// </summary>
public class ProcessCrashRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.ProcessCrash;
    public string RuleId => "process-crash";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var crash = ctx.AgentEvents.LastOrDefault(e => e.EventType == "ProcessCrash");
        if (crash is null) return null;

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "processCrash";
        signal.Observed = 1;
        signal.Threshold = 0;
        signal.Unit = string.Empty;
        signal.Severity = IncidentSeverity.Critical;
        signal.Component = crash.Source;
        signal.Symptom = crash.Message;
        return signal;
    }
}

/// <summary>A process the KAIRON Agent was watching reported CPU above its configured
/// threshold.</summary>
public class ProcessHighResourceRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.ProcessHighResource;
    public string RuleId => "process-high-resource";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var events = ctx.AgentEvents.Where(e => e.EventType == "ProcessHighResource").ToList();
        if (events.Count == 0) return null;

        var latest = events[^1];

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "processHighResource";
        signal.Observed = events.Count;
        signal.Threshold = 1;
        signal.Unit = " occurrences";
        signal.Severity = IncidentSeverity.Medium;
        signal.Component = latest.Source;
        signal.Symptom = latest.Message;
        return signal;
    }
}

/// <summary>Queue backlog. Not in the PRD's initial list by name, but it is the signal the demo
/// scenario emits (frontend PRD section 15) and it correlates with the retry storm.</summary>
public class QueueBacklogRule : IDetectionRule
{
    public DetectionRuleKind Kind => DetectionRuleKind.MetricDeviation;
    public string RuleId => "queue-backlog";

    public DetectionSignal? Evaluate(DetectionContext ctx)
    {
        var threshold = ctx.Options.QueueDepthThreshold;
        var withQueue = ctx.Metrics.Where(m => m.QueueDepth.HasValue).ToList();
        if (withQueue.Count < ctx.Options.MinimumSamples) return null;

        var peak = withQueue.Max(m => m.QueueDepth ?? 0);
        if (peak <= threshold) return null;

        var signal = ctx.NewSignal(Kind, RuleId);
        signal.MetricName = "queue";
        signal.Observed = peak;
        signal.Threshold = threshold;
        signal.Unit = " items";
        signal.Severity = ctx.SeverityFor(peak, threshold);
        signal.Symptom = $"Queue depth reached {peak} items, threshold {threshold}";
        return signal;
    }
}
