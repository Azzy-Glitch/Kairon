using System.Collections.Concurrent;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Remediation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Detection;

public interface IDetectionEngine
{
    /// <summary>
    /// Runs every deterministic rule for one project/environment/service scope and returns the
    /// signals that survived dedup and cooldown.
    /// </summary>
    Task<IReadOnlyList<DetectionSignal>> EvaluateAsync(
        Guid projectId,
        string environment,
        string? service = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default);

    /// <summary>Rule evaluation against an already-built context. Used by tests and by callers
    /// that already hold the telemetry window.</summary>
    IReadOnlyList<DetectionSignal> Evaluate(DetectionContext context);
}

/// <summary>
/// Suppresses a signal that has already fired recently (PRD section 7: dedup + cooldown).
/// In-memory by design: cooldown is an operational nicety, not durable state, and losing it on
/// restart only costs one duplicate signal.
/// </summary>
public interface IDetectionCooldownStore
{
    bool TryEnter(string dedupKey, DateTime now, TimeSpan cooldown);
    void Clear();
}

public class InMemoryDetectionCooldownStore : IDetectionCooldownStore
{
    private readonly ConcurrentDictionary<string, DateTime> _lastFired = new();

    public bool TryEnter(string dedupKey, DateTime now, TimeSpan cooldown)
    {
        var entered = false;

        _lastFired.AddOrUpdate(
            dedupKey,
            _ =>
            {
                entered = true;
                return now;
            },
            (_, previous) =>
            {
                if (now - previous >= cooldown)
                {
                    entered = true;
                    return now;
                }

                return previous;
            });

        return entered;
    }

    public void Clear() => _lastFired.Clear();
}

public class DetectionEngine : IDetectionEngine
{
    private readonly AppDbContext _db;
    private readonly IEnumerable<IDetectionRule> _rules;
    private readonly IDetectionCooldownStore _cooldown;
    private readonly DetectionOptions _options;
    private readonly IRemediationTargetResolver _targets;
    private readonly ILogger<DetectionEngine> _logger;

    public DetectionEngine(
        AppDbContext db,
        IEnumerable<IDetectionRule> rules,
        IDetectionCooldownStore cooldown,
        IOptions<DetectionOptions> options,
        ILogger<DetectionEngine> logger, IRemediationTargetResolver targets)
    {
        _db = db;
        _rules = rules;
        _cooldown = cooldown;
        _options = options.Value;
        _targets = targets;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DetectionSignal>> EvaluateAsync(
        Guid projectId,
        string environment,
        string? service = null,
        DateTime? now = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return Array.Empty<DetectionSignal>();

        // A sender can retry queued telemetry after its project was deleted, and legacy data may
        // predate project registration. Neither case is a connected app, so it must never enter
        // the autonomous incident pipeline.
        if (!await _db.Projects.AsNoTracking()
                .AnyAsync(p => p.Id == projectId && p.IsActive, cancellationToken))
        {
            _logger.LogDebug("Skipping detection for unregistered project {ProjectId}", projectId);
            return Array.Empty<DetectionSignal>();
        }

        var evaluatedAt = now ?? DateTime.UtcNow;
        var windowStart = evaluatedAt.AddSeconds(-_options.EvaluationWindowSeconds);

        var metricsQuery = _db.Metrics
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId
                        && m.Environment == environment
                        && m.Timestamp >= windowStart
                        && m.Timestamp <= evaluatedAt);

        var telemetryQuery = _db.Incidents
            .AsNoTracking()
            .Where(i => i.ProjectId == projectId
                        && i.Environment == environment
                        && i.Timestamp >= windowStart
                        && i.Timestamp <= evaluatedAt);

        var agentEventsQuery = _db.AgentEvents
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId
                        && e.Environment == environment
                        && e.Timestamp >= windowStart
                        && e.Timestamp <= evaluatedAt);

        if (!string.IsNullOrWhiteSpace(service))
        {
            metricsQuery = metricsQuery.Where(m => m.Service == service);
            telemetryQuery = telemetryQuery.Where(i => i.Service == service);
            agentEventsQuery = agentEventsQuery.Where(e => e.Service == service);
        }

        // A null/blank service scope never matches a configured target (a real target always has
        // a concrete service name), matching the prior in-memory comparison's behavior exactly.
        var resolution = string.IsNullOrWhiteSpace(service)
            ? DetectionTargetResolution.None
            : await _targets.ResolveDetectionTargetAsync(projectId, environment, service, cancellationToken);
        if (resolution.Outcome == DetectionTargetOutcome.Ambiguous) return Array.Empty<DetectionSignal>();
        Guid? machineId = resolution.Outcome == DetectionTargetOutcome.Unique ? resolution.MachineId : null;
        var correlationKey = $"{projectId}|{environment}|{service}" + (machineId.HasValue ? $"|{machineId}" : "");
        var recoveredAt = await _db.SreIncidents.AsNoTracking()
            .Where(i => i.ProjectId == projectId && i.CorrelationKey == correlationKey &&
                i.Status == IncidentStatus.Resolved && i.VerificationState == VerificationStatus.Passed && i.ResolvedAt != null)
            .OrderByDescending(i => i.ResolvedAt).Select(i => i.ResolvedAt).FirstOrDefaultAsync(cancellationToken);
        if (recoveredAt.HasValue) {
            // A verified recovery closes the old fault window. A recurrence must be supported
            // by new observations, not the same pre-restart errors aging through the window.
            metricsQuery = metricsQuery.Where(m => m.Timestamp > recoveredAt.Value);
            telemetryQuery = telemetryQuery.Where(i => i.Timestamp > recoveredAt.Value);
            agentEventsQuery = agentEventsQuery.Where(e => e.Timestamp > recoveredAt.Value);
        }
        if (machineId.HasValue) {
            metricsQuery = metricsQuery.Where(m => m.MachineId == machineId);
            telemetryQuery = telemetryQuery.Where(i => i.MachineId == machineId);
            // Current agent events lack authenticated machine scope. They remain available in
            // inventory/history, but cannot trigger remediation against an enrolled service.
            agentEventsQuery = agentEventsQuery.Where(e => false);
        }

        var metrics = await metricsQuery
            .OrderBy(m => m.Timestamp)
            // Bounded so an unusually chatty window cannot pull the whole table into memory.
            .Take(500)
            .ToListAsync(cancellationToken);

        var telemetry = await telemetryQuery
            .OrderBy(i => i.Timestamp)
            .Take(500)
            .ToListAsync(cancellationToken);

        var agentEvents = await agentEventsQuery
            .OrderBy(e => e.Timestamp)
            .Take(500)
            .ToListAsync(cancellationToken);

        if (metrics.Count == 0 && telemetry.Count == 0 && agentEvents.Count == 0)
            return Array.Empty<DetectionSignal>();

        var resolvedService = service
            ?? metrics.Select(m => m.Service).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? telemetry.Select(t => t.Service).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? agentEvents.Select(e => e.Service).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? "Unknown";

        var application = metrics.Select(m => m.Application).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? telemetry.Select(t => t.Application).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? agentEvents.Select(e => e.Application).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
            ?? "Unknown";

        var context = new DetectionContext
        {
            Options = _options,
            ProjectId = projectId,
            MachineId = machineId,
            Environment = environment,
            Service = resolvedService,
            Application = application,
            Metrics = metrics,
            Telemetry = telemetry,
            AgentEvents = agentEvents,
            Now = evaluatedAt
        };

        return Evaluate(context);
    }

    public IReadOnlyList<DetectionSignal> Evaluate(DetectionContext context)
    {
        if (!context.Options.Enabled)
            return Array.Empty<DetectionSignal>();

        var signals = new List<DetectionSignal>();
        var cooldown = TimeSpan.FromSeconds(context.Options.CooldownSeconds);

        foreach (var rule in _rules)
        {
            DetectionSignal? signal;

            try
            {
                signal = rule.Evaluate(context);
            }
            catch (Exception ex)
            {
                // One broken rule must not blind the whole engine.
                _logger.LogError(ex, "Detection rule {RuleId} threw; skipping it for this evaluation", rule.RuleId);
                continue;
            }

            if (signal is null)
                continue;

            signal.MachineId = context.MachineId;
            if (!_cooldown.TryEnter(signal.DedupKey, context.Now, cooldown))
            {
                _logger.LogDebug("Signal {RuleId} for {Service} suppressed by cooldown", signal.RuleId, signal.Service);
                continue;
            }

            signals.Add(signal);
        }

        if (signals.Count > 0)
        {
            _logger.LogInformation(
                "Detection produced {Count} signal(s) for project {ProjectId} service {Service}: {Rules}",
                signals.Count, context.ProjectId, context.Service, string.Join(", ", signals.Select(s => s.RuleId)));
        }

        return signals;
    }
}
