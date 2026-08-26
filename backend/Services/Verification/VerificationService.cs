using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Verification;

public interface IVerificationService
{
    /// <summary>
    /// Compares fresh telemetry against the pre-remediation baseline and decides whether the
    /// incident actually recovered (PRD section 13). The backend is authoritative here - the UI
    /// never gets to decide an incident is resolved.
    /// </summary>
    Task<VerificationResult> VerifyAsync(
        SreIncident incident,
        RemediationAction action,
        CancellationToken cancellationToken = default);
}

public class VerificationService : IVerificationService
{
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;
    private readonly IRemediationToolRegistryAccessor _tools;
    private readonly VerificationOptions _options;
    private readonly DetectionOptions _detection;
    private readonly ILogger<VerificationService> _logger;

    public VerificationService(
        AppDbContext db,
        IAuditService audit,
        IRemediationToolRegistryAccessor tools,
        IOptions<VerificationOptions> options,
        IOptions<DetectionOptions> detection,
        ILogger<VerificationService> logger)
    {
        _db = db;
        _audit = audit;
        _tools = tools;
        _options = options.Value;
        _detection = detection.Value;
        _logger = logger;
    }

    public async Task<VerificationResult> VerifyAsync(
        SreIncident incident,
        RemediationAction action,
        CancellationToken cancellationToken = default)
    {
        var verification = new VerificationResult
        {
            IncidentId = incident.Id,
            ActionId = action.Id,
            StartedAt = DateTime.UtcNow,
            Status = VerificationStatus.Pending
        };

        _db.VerificationResults.Add(verification);
        incident.Verifications.Add(verification);

        _audit.Record(incident, IncidentEventTypes.Verifying, "verification-service",
            actionId: action.ActionKey,
            message: $"Waiting {_options.SettleSeconds}s for fresh telemetry before comparing");

        await _db.SaveChangesAsync(cancellationToken);

        var executedAt = action.CompletedAt ?? action.StartedAt ?? DateTime.UtcNow;

        // "Before" is the degraded period, ending the moment the remediation ran.
        var beforeWindowStart = incident.Timestamp.AddSeconds(-_detection.EvaluationWindowSeconds);
        var before = await SampleAsync(incident, beforeWindowStart, executedAt, cancellationToken);

        // "After" deliberately starts at the end of the settle period rather than at execution.
        // A fix takes time to take effect, and averaging from the moment it ran folds the whole
        // recovery ramp into the result - which reads as "still breaching" even when the service
        // has fully recovered. What matters is the state it settled at.
        var settleFrom = executedAt.AddSeconds(_options.SettleSeconds);
        var after = await WaitForSettledTelemetryAsync(incident, settleFrom, cancellationToken);

        var comparisons = BuildComparisons(incident, before, after);

        verification.CompletedAt = DateTime.UtcNow;
        verification.ComparisonsJson = SreJson.Serialize(comparisons);

        if (after.SampleCount == 0)
        {
            // No fresh telemetry means we genuinely do not know. Saying "inconclusive" is the
            // honest answer; claiming recovery here would be the worst possible failure mode.
            verification.Status = VerificationStatus.Inconclusive;
            verification.RecoveryScore = 0;
            verification.Summary = "No telemetry was received after remediation, so recovery could not be confirmed.";
            verification.FailureReason = "no-post-remediation-telemetry";
        }
        else
        {
            var checkedComparisons = comparisons.Where(c => c.Before.HasValue && c.After.HasValue).ToList();

            verification.RecoveryScore = checkedComparisons.Count == 0
                ? 0
                : (double)checkedComparisons.Count(c => c.MeetsThreshold) / checkedComparisons.Count;

            var passed = checkedComparisons.Count > 0 &&
                         verification.RecoveryScore >= _options.RequiredRecoveryScore;

            verification.Status = passed ? VerificationStatus.Passed : VerificationStatus.Failed;
            verification.Summary = passed
                ? $"Recovery confirmed: {checkedComparisons.Count(c => c.MeetsThreshold)}/{checkedComparisons.Count} metrics returned within threshold."
                : $"Recovery not confirmed: only {checkedComparisons.Count(c => c.MeetsThreshold)}/{checkedComparisons.Count} metrics returned within threshold.";

            if (!passed)
            {
                verification.FailureReason = checkedComparisons.Count == 0
                    ? "no-comparable-metrics"
                    : "metrics-still-breaching";
            }
        }

        incident.VerificationState = verification.Status;
        action.VerificationResultId = verification.Id;

        _audit.Record(incident, IncidentEventTypes.Verified, "verification-service",
            actionId: action.ActionKey,
            result: verification.Status.ToString(),
            message: verification.Summary,
            data: comparisons);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Verification for {IncidentKey} action {ActionKey}: {Status} (score {Score:P0})",
            incident.IncidentKey, action.ActionKey, verification.Status, verification.RecoveryScore);

        return verification;
    }

    /// <summary>
    /// Waits for enough post-settle telemetry to make a comparison meaningful, then returns it.
    ///
    /// Bounded by <see cref="VerificationOptions.MaxWaitSeconds"/>: if the samples never arrive the
    /// window comes back empty and the verification is reported inconclusive, which is the honest
    /// answer when nothing has been observed.
    /// </summary>
    private async Task<MetricWindow> WaitForSettledTelemetryAsync(
        SreIncident incident,
        DateTime settleFrom,
        CancellationToken cancellationToken)
    {
        var deadline = settleFrom.AddSeconds(Math.Max(0, _options.MaxWaitSeconds));

        while (true)
        {
            var remaining = settleFrom - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
                continue;
            }

            var window = await SampleAsync(
                incident, settleFrom, settleFrom.AddSeconds(_options.WindowSeconds), cancellationToken);

            if (window.MetricSampleCount >= _options.MinimumSamples)
                return window;

            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning(
                    "Verification for {Key} timed out waiting for settled telemetry ({Count} sample(s) after {Wait}s)",
                    incident.IncidentKey, window.MetricSampleCount, _options.MaxWaitSeconds);

                return window;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<MetricWindow> SampleAsync(
        SreIncident incident,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var metrics = await _db.Metrics
            .AsNoTracking()
            .Where(m => m.ProjectId == incident.ProjectId
                        && m.Environment == incident.Environment
                        && m.Timestamp >= from
                        && m.Timestamp <= to)
            .OrderBy(m => m.Timestamp)
            .Take(300)
            .ToListAsync(cancellationToken);

        var telemetry = await _db.Incidents
            .AsNoTracking()
            .Where(i => i.ProjectId == incident.ProjectId
                        && i.Environment == incident.Environment
                        && i.Timestamp >= from
                        && i.Timestamp <= to)
            .Take(500)
            .ToListAsync(cancellationToken);

        return MetricWindow.From(metrics, telemetry);
    }

    /// <summary>
    /// Builds the before/after table the UI renders. Only metrics the incident actually breached
    /// are compared, plus whatever the executed tool was expected to move - comparing everything
    /// would dilute the score with metrics that were never part of the problem.
    /// </summary>
    private List<MetricComparison> BuildComparisons(
        SreIncident incident,
        MetricWindow before,
        MetricWindow after)
    {
        var breached = SreJson
            .Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>())
            .Select(s => s.MetricName)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var action in incident.Actions.Where(a => a.Status == RemediationStatus.Executed))
        {
            if (_tools.TryGet(action.ActionType, out var tool))
            {
                foreach (var metric in tool.ExpectedMetricEffects)
                    breached.Add(metric);
            }
        }

        if (breached.Count == 0)
            breached = new HashSet<string>(new[] { "cpu", "latency", "errorRate" }, StringComparer.OrdinalIgnoreCase);

        var definitions = new (string Name, string Unit, double Threshold, Func<MetricWindow, double?> Selector)[]
        {
            ("cpu", "%", _detection.CpuPercentThreshold, w => w.Cpu),
            ("memory", "%", _detection.MemoryPercentThreshold, w => w.Memory),
            ("latency", "ms", _detection.LatencyMsThreshold, w => w.Latency),
            ("errorRate", "%", _detection.ErrorRateThreshold * 100, w => w.ErrorRatePercent),
            ("retries", "/min", _detection.RetryStormPerMinute, w => w.RetriesPerMinute),
            ("queue", " items", _detection.QueueDepthThreshold, w => w.QueueDepth)
        };

        var comparisons = new List<MetricComparison>();

        foreach (var (name, unit, threshold, selector) in definitions)
        {
            if (!breached.Contains(name))
                continue;

            var beforeValue = selector(before);
            var afterValue = selector(after);

            var comparison = new MetricComparison
            {
                Metric = name,
                Before = beforeValue.HasValue ? Math.Round(beforeValue.Value, 1) : null,
                After = afterValue.HasValue ? Math.Round(afterValue.Value, 1) : null,
                Unit = unit,
                Threshold = threshold,
                Improved = beforeValue.HasValue && afterValue.HasValue && afterValue.Value < beforeValue.Value,
                MeetsThreshold = afterValue.HasValue && afterValue.Value <= threshold
            };

            comparisons.Add(comparison);
        }

        return comparisons;
    }

    /// <summary>Aggregated view of one telemetry window, so before/after math stays in one place.</summary>
    private class MetricWindow
    {
        public double? Cpu { get; private init; }
        public double? Memory { get; private init; }
        public double? Latency { get; private init; }
        public double? ErrorRatePercent { get; private init; }
        public double? RetriesPerMinute { get; private init; }
        public double? QueueDepth { get; private init; }
        public int SampleCount { get; private init; }

        /// <summary>Metric samples only. Telemetry rows alone cannot answer "did the metrics recover".</summary>
        public int MetricSampleCount { get; private init; }

        public static MetricWindow From(List<Metric> metrics, List<Incident> telemetry)
        {
            if (metrics.Count == 0 && telemetry.Count == 0)
                return new MetricWindow { SampleCount = 0, MetricSampleCount = 0 };

            double? Avg(Func<Metric, double?> selector)
            {
                var values = metrics.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                return values.Count == 0 ? null : values.Average();
            }

            long requests = metrics.Sum(m => m.RequestCount);
            long errors = metrics.Sum(m => m.ErrorCount);

            if (requests == 0 && telemetry.Count > 0)
            {
                requests = telemetry.Count;
                errors = telemetry.Count(t => t.StatusCode >= 500 || !string.IsNullOrEmpty(t.ErrorMessage));
            }

            var spanMinutes = metrics.Count > 1
                ? Math.Max((metrics[^1].Timestamp - metrics[0].Timestamp).TotalMinutes, 1.0 / 60)
                : 1.0;

            var retryTotal = metrics.Sum(m => m.RetryCount ?? 0);

            var latency = Avg(m => m.ResponseTimeMs)
                ?? (telemetry.Count > 0 ? telemetry.Average(t => (double)t.DurationMs) : null);

            return new MetricWindow
            {
                Cpu = Avg(m => m.CpuPercent),
                Memory = Avg(m => m.MemoryPercent),
                Latency = latency,
                ErrorRatePercent = requests > 0 ? (double)errors / requests * 100 : null,
                RetriesPerMinute = metrics.Any(m => m.RetryCount.HasValue) ? retryTotal / spanMinutes : null,
                QueueDepth = Avg(m => m.QueueDepth),
                SampleCount = metrics.Count + telemetry.Count,
                MetricSampleCount = metrics.Count
            };
        }
    }
}

/// <summary>
/// Narrow read-only view of the tool registry. Verification only needs to ask what a tool was
/// meant to affect, so it takes this rather than the full registry.
/// </summary>
public interface IRemediationToolRegistryAccessor
{
    bool TryGet(string name, out Remediation.IRemediationTool tool);
}

public class RemediationToolRegistryAccessor : IRemediationToolRegistryAccessor
{
    private readonly Remediation.IRemediationToolRegistry _registry;

    public RemediationToolRegistryAccessor(Remediation.IRemediationToolRegistry registry) => _registry = registry;

    public bool TryGet(string name, out Remediation.IRemediationTool tool) => _registry.TryGet(name, out tool);
}
