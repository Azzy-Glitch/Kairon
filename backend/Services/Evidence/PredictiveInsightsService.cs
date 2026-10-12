using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Evidence;

/// <summary>A risk the measurements suggest is emerging - not a current incident, and not a certainty.</summary>
public sealed class EmergingRiskDto
{
    /// <summary>capacity | precursor | backlog | dependency | recurrence</summary>
    public string Kind { get; set; } = string.Empty;
    public Guid ProjectId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    /// <summary>The metric, dependency target or fault kind the risk concerns.</summary>
    public string Component { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>The observed trend, with its numbers.</summary>
    public string Observation { get; set; } = string.Empty;
    public string Consequence { get; set; } = string.Empty;
    public string PreventiveAction { get; set; } = string.Empty;
    /// <summary>How well the data supports this (fit quality and sample count), 0..1. Not a probability.</summary>
    public double Confidence { get; set; }
    /// <summary>Minutes until the threshold is reached IF the observed linear trend continues. Only set
    /// when the trend is consistent enough to extrapolate; never a predicted failure time.</summary>
    public int? HorizonMinutes { get; set; }
    public int Samples { get; set; }
    public int WindowMinutes { get; set; }
}

public sealed class InsufficientDataDto
{
    public Guid ProjectId { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class EmergingRisksDto
{
    public DateTime GeneratedAt { get; set; }
    public int WindowMinutes { get; set; }
    public List<EmergingRiskDto> Risks { get; set; } = new();
    public List<InsufficientDataDto> InsufficientData { get; set; } = new();
}

public interface IPredictiveInsightsService
{
    Task<EmergingRisksDto> GetEmergingRisksAsync(Guid? projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Emerging operational risks from data KAIRON already stores - the last hour of each service's
/// metric samples, its dependency calls and its incident history. Deterministic and explainable:
/// least-squares trends with a fit-quality gate, compared against the detection thresholds. A metric
/// already over its threshold is a current problem (detection's job), never reported as a prediction;
/// a horizon is given only when the trend is consistent enough to extrapolate.
/// </summary>
public sealed class PredictiveInsightsService(AppDbContext db, IOptions<DetectionOptions> detection) : IPredictiveInsightsService
{
    public const int WindowMinutes = 60;
    internal const int MinimumSamples = 12;
    internal const double MinimumSpanMinutes = 10;
    internal const double MinimumFit = 0.6;
    internal const int MaxHorizonMinutes = 6 * 60;
    /// <summary>A scope whose newest sample is older than this is not projected: the trend may have ended.</summary>
    internal const double StaleAfterMinutes = 5;
    private const int MaxScopes = 25;
    private const int MaxSamplesPerScope = 720;

    private readonly DetectionOptions _thresholds = detection.Value;

    public async Task<EmergingRisksDto> GetEmergingRisksAsync(Guid? projectId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var since = now.AddMinutes(-WindowMinutes);
        var result = new EmergingRisksDto { GeneratedAt = now, WindowMinutes = WindowMinutes };

        var metricsQuery = db.Metrics.AsNoTracking()
            .Where(m => m.Timestamp >= since && db.Projects.Any(p => p.Id == m.ProjectId && p.IsActive));
        if (projectId.HasValue) metricsQuery = metricsQuery.Where(m => m.ProjectId == projectId.Value);

        var scopes = await metricsQuery
            .GroupBy(m => new { m.ProjectId, m.Environment, m.Service })
            .Select(g => new { g.Key.ProjectId, g.Key.Environment, g.Key.Service, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(MaxScopes)
            .ToListAsync(cancellationToken);

        foreach (var scope in scopes)
        {
            var samples = await metricsQuery
                .Where(m => m.ProjectId == scope.ProjectId && m.Environment == scope.Environment && m.Service == scope.Service)
                .OrderByDescending(m => m.Timestamp)
                .Take(MaxSamplesPerScope)
                .Select(m => new Sample(m.Timestamp, m.CpuPercent, m.MemoryPercent, m.ResponseTimeMs, m.RequestCount, m.ErrorCount, m.RetryCount, m.QueueDepth))
                .ToListAsync(cancellationToken);
            samples.Reverse();

            var span = samples.Count > 1 ? (samples[^1].At - samples[0].At).TotalMinutes : 0;
            var age = samples.Count > 0 ? (now - samples[^1].At).TotalMinutes : double.MaxValue;
            if (samples.Count > 0 && age > StaleAfterMinutes)
            {
                result.InsufficientData.Add(new InsufficientDataDto
                {
                    ProjectId = scope.ProjectId, Environment = scope.Environment, Service = scope.Service,
                    Reason = $"The data is stale: the newest sample is {age:0} minutes old, so a trend that may already have ended is not projected."
                });
            }
            else if (samples.Count < MinimumSamples || span < MinimumSpanMinutes)
            {
                result.InsufficientData.Add(new InsufficientDataDto
                {
                    ProjectId = scope.ProjectId, Environment = scope.Environment, Service = scope.Service,
                    Reason = $"{samples.Count} sample(s) over {span:0} minute(s) in the last hour - at least {MinimumSamples} over {MinimumSpanMinutes:0} minutes are needed to judge a trend."
                });
            }
            else
            {
                result.Risks.AddRange(TrendRisks(scope.ProjectId, scope.Environment, scope.Service, samples));
            }
        }

        result.Risks.AddRange(await DependencyRisksAsync(projectId, since, cancellationToken));
        result.Risks.AddRange(await RecurrenceRisksAsync(projectId, now, cancellationToken));
        result.Risks = result.Risks.OrderByDescending(r => r.HorizonMinutes.HasValue).ThenBy(r => r.HorizonMinutes ?? int.MaxValue)
            .ThenByDescending(r => r.Confidence).ToList();
        return result;
    }

    internal sealed record Sample(DateTime At, double? Cpu, double? Memory, double? Latency, long Requests, long Errors, long? Retries, long? Queue);

    internal IEnumerable<EmergingRiskDto> TrendRisks(Guid projectId, string environment, string service, IReadOnlyList<Sample> samples)
    {
        EmergingRiskDto Risk(string kind, string component, string title, Fit fit, string unit, string consequence, string preventive, int? horizon) => new()
        {
            Kind = kind, ProjectId = projectId, Environment = environment, Service = service, Component = component, Title = title,
            Observation = $"{title}: from about {fit.Start:0.#}{unit} to {fit.End:0.#}{unit} over {fit.SpanMinutes:0} minutes " +
                          $"({fit.SlopePerMinute * 60:+0.#;-0.#}{unit}/hour, trend fit R² {fit.R2:0.00}).",
            Consequence = consequence, PreventiveAction = preventive, HorizonMinutes = horizon,
            Confidence = Math.Round(Math.Min(0.9, fit.R2 * Math.Min(1, fit.Count / 30.0)), 2),
            Samples = fit.Count, WindowMinutes = WindowMinutes
        };

        // Retries are counted per sample; the threshold is per minute, so convert using each sample's
        // real interval (collection can be irregular).
        var retriesPerMinute = new List<(DateTime At, double Value)>();
        for (var i = 1; i < samples.Count; i++)
        {
            var minutes = (samples[i].At - samples[i - 1].At).TotalMinutes;
            if (samples[i].Retries is { } r && minutes > 0) retriesPerMinute.Add((samples[i].At, r / minutes));
        }

        // Resources climbing toward exhaustion.
        foreach (var (metric, label, threshold, pick, consequence, preventive) in new (string, string, double, Func<Sample, double?>, string, string)[]
                 {
                     ("cpu", "CPU rising", _thresholds.CpuPercentThreshold, s => s.Cpu,
                         "Requests slow down for everyone on this process once CPU saturates.",
                         "Find what is consuming more CPU (traffic growth, a background job, an expensive endpoint) and add capacity or reduce work before it saturates."),
                     ("memory", "Memory rising", _thresholds.MemoryPercentThreshold, s => s.Memory,
                         "The process may be killed or crash when memory runs out.",
                         "Check for a leak or an unbounded cache in the workload that grows; a restart only resets the clock.")
                 })
        {
            if (Steady(Points(samples, pick)) is not { } fit) continue;
            if (fit.End >= threshold || RecentlyAtOrOver(samples, pick, threshold)) continue; // a current problem, not a forecast
            var minutes = (threshold - fit.End) / fit.SlopePerMinute;
            if (minutes > MaxHorizonMinutes) continue;
            yield return Risk("capacity", metric, label, fit, "%", consequence, preventive, (int)Math.Ceiling(minutes));
        }

        // Latency climbing while errors are still under threshold - slowdowns often turn into timeouts.
        if (Steady(Points(samples, s => s.Latency), 0.5) is { } latency
            && latency.End < _thresholds.LatencyMsThreshold && !RecentlyAtOrOver(samples, s => s.Latency, _thresholds.LatencyMsThreshold)
            && latency.End >= _thresholds.LatencyMsThreshold * 0.5 && latency.End >= latency.Start * 1.5)
        {
            var requests = samples.Sum(s => s.Requests);
            var errorRate = requests > 0 ? (double)samples.Sum(s => s.Errors) / requests : 0;
            if (errorRate <= _thresholds.ErrorRateThreshold)
            {
                var minutes = (_thresholds.LatencyMsThreshold - latency.End) / latency.SlopePerMinute;
                yield return Risk("precursor", "latency", "Latency rising", latency, "ms",
                    "Rising latency often precedes timeouts and errors, especially when callers have timeouts set.",
                    "Find what is getting slower (a dependency, database queries, contention) before it starts causing errors.",
                    minutes <= MaxHorizonMinutes ? (int)Math.Ceiling(minutes) : null);
            }
        }

        // Backlog and retries building up.
        foreach (var (metric, label, threshold, points, unit, preventive) in new (string, string, double, List<(DateTime At, double Value)>, string, string)[]
                 {
                     ("queue", "Queue depth rising", _thresholds.QueueDepthThreshold, Points(samples, s => s.Queue), "",
                         "Check worker capacity and per-item processing time, and whether a downstream step became slower."),
                     ("retries", "Retries rising", _thresholds.RetryStormPerMinute, retriesPerMinute, "/min",
                         "Find which call is being retried and why; make retries bounded with backoff so they cannot amplify a failure.")
                 })
        {
            if (Steady(points, 0.5) is not { } fit || fit.End < Math.Max(1, threshold * 0.3)) continue;
            yield return Risk("backlog", metric, label, fit, unit,
                "If the trend continues the service degrades: work waits longer and a retry storm can overload what it depends on.",
                preventive, null);
        }
    }

    private async Task<List<EmergingRiskDto>> DependencyRisksAsync(Guid? projectId, DateTime since, CancellationToken cancellationToken)
    {
        var rows = db.Incidents.AsNoTracking()
            .Where(i => i.Timestamp >= since && i.Method == "DEPENDENCY" && db.Projects.Any(p => p.Id == i.ProjectId && p.IsActive));
        if (projectId.HasValue) rows = rows.Where(i => i.ProjectId == projectId.Value);
        var groups = await rows
            .GroupBy(i => new { i.ProjectId, i.Environment, i.Service, i.Endpoint })
            .Select(g => new { g.Key.ProjectId, g.Key.Environment, g.Key.Service, g.Key.Endpoint, Calls = g.Count(),
                               Failures = g.Count(x => x.StatusCode >= 500), AvgMs = g.Average(x => (double)x.DurationMs) })
            .ToListAsync(cancellationToken);
        return groups.Where(g => g.Failures >= 3 && g.Failures >= g.Calls * 0.2).Select(g => new EmergingRiskDto
        {
            Kind = "dependency", ProjectId = g.ProjectId, Environment = g.Environment, Service = g.Service, Component = g.Endpoint,
            Title = "Repeated dependency failures",
            Observation = $"{g.Failures} of {g.Calls} calls to {g.Endpoint} failed in the last hour (avg {g.AvgMs:0} ms).",
            Consequence = "Requests that depend on it will keep failing or slowing down, and retries add load to it.",
            PreventiveAction = $"Check {g.Endpoint}'s availability and limits, and give the app timeouts and a circuit breaker for it.",
            Confidence = Math.Round(Math.Min(0.9, 0.4 + 0.05 * g.Failures), 2), Samples = g.Calls, WindowMinutes = WindowMinutes
        }).ToList();
    }

    private async Task<List<EmergingRiskDto>> RecurrenceRisksAsync(Guid? projectId, DateTime now, CancellationToken cancellationToken)
    {
        var since = now.AddHours(-24);
        var incidents = db.SreIncidents.AsNoTracking()
            .Where(i => i.Timestamp >= since && db.Projects.Any(p => p.Id == i.ProjectId && p.IsActive));
        if (projectId.HasValue) incidents = incidents.Where(i => i.ProjectId == projectId.Value);
        var rows = await incidents.Select(i => new { i.ProjectId, i.Environment, i.Service, i.CorrelatedMetricsJson, i.Timestamp })
            .ToListAsync(cancellationToken);

        return rows
            .SelectMany(r => FaultCategories.Of(SreJson.Deserialize(r.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>()))
                .Select(kind => (r.ProjectId, r.Environment, r.Service, Kind: kind, r.Timestamp)))
            .GroupBy(x => (x.ProjectId, x.Environment, x.Service, x.Kind))
            .Where(g => g.Count() >= 3)
            .Select(g => new EmergingRiskDto
            {
                Kind = "recurrence", ProjectId = g.Key.ProjectId, Environment = g.Key.Environment, Service = g.Key.Service,
                Component = g.Key.Kind, Title = $"Recurring {g.Key.Kind} incidents",
                Observation = $"{g.Count()} incidents with {g.Key.Kind} symptoms in the last 24 hours (latest {g.Max(x => x.Timestamp):HH:mm} UTC).",
                Consequence = "The same problem keeps coming back; mitigations are not removing its cause.",
                PreventiveAction = "Treat it as one problem: compare these incidents' evidence and fix the shared cause instead of mitigating each one.",
                Confidence = Math.Round(Math.Min(0.9, 0.3 + 0.1 * g.Count()), 2), Samples = g.Count(), WindowMinutes = 24 * 60
            }).ToList();
    }

    internal sealed record Fit(int Count, double SpanMinutes, double SlopePerMinute, double R2, double Start, double End);

    private static List<(DateTime At, double Value)> Points(IEnumerable<Sample> samples, Func<Sample, double?> pick) =>
        samples.Where(s => pick(s).HasValue).Select(s => (s.At, pick(s)!.Value)).ToList();

    private static bool RecentlyAtOrOver(IReadOnlyList<Sample> samples, Func<Sample, double?> pick, double threshold) =>
        samples.Select(pick).Where(v => v.HasValue).TakeLast(3).Any(v => v >= threshold);

    /// <summary>
    /// A rising trend that is safe to extrapolate: enough samples over enough time, a good overall fit,
    /// AND rising at a comparable rate in both halves of the window. A good fit alone is not enough - a
    /// step change in workload (flat, then flat at a higher level) fits a line well but is not climbing.
    /// </summary>
    internal static Fit? Steady(List<(DateTime At, double Value)> points, double minimumFit = MinimumFit)
    {
        if (Trend(points) is not { } fit || fit.Count < MinimumSamples || fit.SpanMinutes < MinimumSpanMinutes
            || fit.R2 < minimumFit || fit.SlopePerMinute <= 0) return null;
        var middle = points[0].At.AddMinutes(fit.SpanMinutes / 2);
        if (Trend(points.Where(p => p.At <= middle).ToList()) is not { } early
            || Trend(points.Where(p => p.At > middle).ToList()) is not { } late) return null;
        if (early.SlopePerMinute <= 0 || late.SlopePerMinute <= 0) return null;
        var ratio = late.SlopePerMinute / early.SlopePerMinute;
        return ratio is >= 1.0 / 3 and <= 3 ? fit : null;
    }

    /// <summary>Least-squares line through (minutes, value); Start/End are the fitted values at the
    /// window's ends, so one noisy sample cannot make the trend look steeper than it is.</summary>
    internal static Fit? Trend(List<(DateTime At, double Value)> points)
    {
        if (points.Count < 3) return null;
        var t0 = points[0].At;
        var xs = points.Select(p => (p.At - t0).TotalMinutes).ToArray();
        var ys = points.Select(p => p.Value).ToArray();
        double mx = xs.Average(), my = ys.Average();
        double sxx = xs.Sum(x => (x - mx) * (x - mx));
        if (sxx <= 0) return null;
        double sxy = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum();
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        double ssTot = ys.Sum(y => (y - my) * (y - my));
        double ssRes = xs.Zip(ys, (x, y) => Math.Pow(y - (intercept + slope * x), 2)).Sum();
        var r2 = ssTot <= 0 ? 0 : Math.Max(0, 1 - ssRes / ssTot);
        return new Fit(points.Count, xs[^1], slope, r2, intercept, intercept + slope * xs[^1]);
    }
}
