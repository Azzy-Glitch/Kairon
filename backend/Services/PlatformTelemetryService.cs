using System.Text.Json;
using System.Data;
using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

/// <summary>
/// Ingests a normalized telemetry batch (docs/DESKTOP_SHELL.md), adapted from Azzy's
/// productization branch - kept near-verbatim, renamed onto this codebase's Project/Environment/
/// Application hierarchy (added additively alongside the existing simple Project entity, see
/// backend/Models/Platform/Project.cs). Idempotent by EventId: a resend is reported as a
/// duplicate, never stored or evaluated twice. Distinct from TelemetryController's existing
/// ingestion (Incident/Metric/AgentEvent) - that pipeline is untouched and keeps working exactly
/// as it does today; this is new, additive capability for callers that want the richer, self-
/// describing normalized shape.
/// </summary>
public interface IPlatformTelemetryService
{
    Task<NormalizedTelemetryResultDto> IngestAsync(NormalizedTelemetryBatchDto batch, CancellationToken cancellationToken);
}

public sealed class PlatformTelemetryService : IPlatformTelemetryService
{
    private readonly AppDbContext _db;
    private readonly IIncidentProcessingQueue _queue;
    private readonly TimeProvider _time;

    public PlatformTelemetryService(AppDbContext db, IIncidentProcessingQueue queue, TimeProvider time)
    {
        _db = db;
        _queue = queue;
        _time = time;
    }

    public async Task<NormalizedTelemetryResultDto> IngestAsync(
        NormalizedTelemetryBatchDto batch, CancellationToken cancellationToken)
    {
        var candidates = batch.Events.Where(IsValid).DistinctBy(x => x.EventId).Take(200).ToList();
        var rejected = batch.Events.Count - candidates.Count;
        var ids = candidates.Select(x => x.EventId).ToList();
        var existing = await _db.TelemetryReceipts.AsNoTracking().Where(x => ids.Contains(x.EventId))
            .Select(x => x.EventId).ToListAsync(cancellationToken);
        var duplicateIds = existing.ToHashSet();
        var accepted = new List<Guid>();
        var evaluations = new HashSet<(Guid Project, string Environment, string Service)>();
        var now = _time.GetUtcNow().UtcDateTime;

        // Serializable keeps concurrent replays from both passing the receipt lookup before the
        // unique event-id row is committed. The batch stays all-or-nothing at the persistence edge.
        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        foreach (var item in candidates.Where(x => !duplicateIds.Contains(x.EventId)))
        {
            var project = await EnsureProjectAsync(item, now, cancellationToken);
            var application = await EnsureApplicationAsync(project, item, now, cancellationToken);
            await EnsureEnvironmentAsync(project, item.Environment, now, cancellationToken);
            var source = await EnsureSourceAsync(project, application, item, now, cancellationToken);
            application.LastTelemetryAt = now;

            _db.TelemetryReceipts.Add(ToReceipt(item, source.Id, now));
            AddCompatibilitySignal(item);
            accepted.Add(item.EventId);
            evaluations.Add((item.ProjectId, EnvironmentOf(item), ServiceOf(item)));
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var evaluation in evaluations)
            _queue.TryEnqueue(new IncidentWorkItem(WorkItemKind.EvaluateDetection,
                evaluation.Project, evaluation.Environment, evaluation.Service));

        return new NormalizedTelemetryResultDto(accepted.Count, duplicateIds.Count, rejected, accepted);
    }

    private async Task<Project> EnsureProjectAsync(NormalizedTelemetryEventDto item, DateTime now,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FindAsync([item.ProjectId], cancellationToken);
        if (project is not null) return project;
        project = new Project
        {
            Id = item.ProjectId, Name = item.Application, Slug = $"project-{item.ProjectId:N}", CreatedAt = now
        };
        _db.Projects.Add(project);
        return project;
    }

    private async Task<MonitoredApplication> EnsureApplicationAsync(Project project,
        NormalizedTelemetryEventDto item, DateTime now, CancellationToken cancellationToken)
    {
        var service = ServiceOf(item);
        var application = _db.MonitoredApplications.Local.SingleOrDefault(
            x => x.ProjectId == project.Id && x.Service == service)
            ?? await _db.MonitoredApplications.SingleOrDefaultAsync(
                x => x.ProjectId == project.Id && x.Service == service, cancellationToken);
        if (application is not null) return application;
        application = new MonitoredApplication
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Name = item.Application, Service = service,
            Runtime = item.Runtime, CreatedAt = now
        };
        _db.MonitoredApplications.Add(application);
        return application;
    }

    private async Task EnsureEnvironmentAsync(Project project, string environment, DateTime now,
        CancellationToken cancellationToken)
    {
        var name = EnvironmentOf(environment);
        if (_db.Environments.Local.Any(x => x.ProjectId == project.Id && x.Name == name)
            || await _db.Environments.AnyAsync(x => x.ProjectId == project.Id && x.Name == name, cancellationToken)) return;
        _db.Environments.Add(new KaironEnvironment { Id = Guid.NewGuid(), ProjectId = project.Id, Name = name, CreatedAt = now });
    }

    private async Task<TelemetrySourceRegistration> EnsureSourceAsync(Project project,
        MonitoredApplication application, NormalizedTelemetryEventDto item, DateTime now,
        CancellationToken cancellationToken)
    {
        var installation = string.IsNullOrWhiteSpace(item.InstallationId)
            ? $"{item.Source}:{application.Id:N}" : item.InstallationId;
        var source = _db.TelemetrySources.Local.SingleOrDefault(
            x => x.ProjectId == project.Id && x.InstallationId == installation)
            ?? await _db.TelemetrySources.SingleOrDefaultAsync(
                x => x.ProjectId == project.Id && x.InstallationId == installation, cancellationToken);
        if (source is null)
        {
            source = new TelemetrySourceRegistration
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, ApplicationId = application.Id,
                SourceType = item.Source, InstallationId = installation, Version = item.SourceVersion,
                RegisteredAt = now
            };
            _db.TelemetrySources.Add(source);
        }
        source.LastSeenAt = now;
        source.IsActive = true;
        return source;
    }

    private static TelemetryReceipt ToReceipt(NormalizedTelemetryEventDto item, Guid sourceId, DateTime now)
    {
        var sanitized = Sanitize(item);
        return new TelemetryReceipt
        {
            Id = Guid.NewGuid(), EventId = item.EventId, ProjectId = item.ProjectId, SourceId = sourceId,
            EventTimestamp = TimestampOf(item), EventType = item.EventType, Severity = item.Severity,
            Source = item.Source, Application = item.Application, Service = ServiceOf(item),
            Environment = EnvironmentOf(item), PayloadJson = Bound(JsonSerializer.Serialize(sanitized), 16000) ?? "{}",
            ReceivedAt = now
        };
    }

    /// <summary>Keeps the existing Incidents/Metrics-driven detection engine and frontend seeing
    /// normalized events too, without duplicating its logic - a normalized "exception"/"http"
    /// event becomes an Incident row, a "metric" event becomes a Metric row, exactly like the
    /// legacy ingestion path already produces.</summary>
    private void AddCompatibilitySignal(NormalizedTelemetryEventDto item)
    {
        var kind = item.EventType.Trim().ToLowerInvariant();
        if (kind is "metric" or "metrics" or "resource" or "resource_metric")
        {
            var values = item.ResourceMetrics ?? new ResourceTelemetryMetricsDto();
            _db.Metrics.Add(new Metric
            {
                Id = Guid.NewGuid(), ProjectId = item.ProjectId, Timestamp = TimestampOf(item),
                CpuPercent = values.CpuPercent, MemoryPercent = values.MemoryPercent,
                ResponseTimeMs = values.ResponseTimeMs, RequestCount = values.RequestCount,
                ErrorCount = values.ErrorCount, RetryCount = values.RetryCount, QueueDepth = values.QueueDepth,
                Application = item.Application, Service = ServiceOf(item), Component = item.Host,
                Environment = EnvironmentOf(item)
            });
            return;
        }

        if (kind is not ("exception" or "http" or "dependency" or "error")) return;
        var http = item.HttpContext;
        var dependency = item.DependencyContext;
        _db.Incidents.Add(new Incident
        {
            Id = Guid.NewGuid(), ProjectId = item.ProjectId, Timestamp = TimestampOf(item),
            Endpoint = Redaction.Scrub(http?.Endpoint ?? dependency?.Target ?? item.Application) ?? item.Application,
            Method = http?.Method ?? (dependency is null ? "EVENT" : "DEPENDENCY"),
            StatusCode = http?.StatusCode ?? (dependency?.Success == false ? 503 : 500),
            DurationMs = http?.DurationMs ?? dependency?.DurationMs ?? 0,
            ErrorType = Bound(Redaction.Scrub(item.ExceptionType), 500),
            ErrorMessage = Bound(Redaction.Scrub(item.Message), 4000),
            StackTrace = Bound(Redaction.Scrub(item.StackTrace), 8000),
            RequestId = Bound(Redaction.Scrub(item.RequestId), 200),
            Severity = item.Severity,
            Environment = EnvironmentOf(item), Application = item.Application, Service = ServiceOf(item),
            MetadataJson = Bound(JsonSerializer.Serialize(Sanitize(item.Metadata)), 16000)
        });
    }

    private static bool IsValid(NormalizedTelemetryEventDto item) => item.EventId != Guid.Empty
        && item.ProjectId != Guid.Empty && !string.IsNullOrWhiteSpace(item.EventType)
        && !string.IsNullOrWhiteSpace(item.Source) && !string.IsNullOrWhiteSpace(item.Application);
    private static DateTime TimestampOf(NormalizedTelemetryEventDto item) => item.Timestamp == default
        ? DateTime.UtcNow
        : UtcDateTimeJsonConverter.Normalize(item.Timestamp);
    private static string ServiceOf(NormalizedTelemetryEventDto item) => string.IsNullOrWhiteSpace(item.Service) ? item.Application : item.Service;
    private static string EnvironmentOf(NormalizedTelemetryEventDto item) => EnvironmentOf(item.Environment);
    private static string EnvironmentOf(string value) => string.IsNullOrWhiteSpace(value) ? "Development" : value;
    private static string? Bound(string? value, int maximum) => value is null || value.Length <= maximum ? value : value[..maximum];
    private static object Sanitize(NormalizedTelemetryEventDto item) => new
    {
        item.EventId, Timestamp = TimestampOf(item), item.EventType, item.Severity, item.Source,
        item.Application, Service = ServiceOf(item), Environment = EnvironmentOf(item), item.Host,
        item.ProcessId, item.Runtime, item.CorrelationId, item.TraceId, item.RequestId,
        Message = Redaction.Scrub(item.Message), Exception = Redaction.Scrub(item.ExceptionType),
        StackTrace = Redaction.Scrub(item.StackTrace),
        HttpContext = item.HttpContext is null ? null : new
        {
            Endpoint = Redaction.Scrub(item.HttpContext.Endpoint), item.HttpContext.Method,
            item.HttpContext.StatusCode, item.HttpContext.DurationMs
        },
        DependencyContext = item.DependencyContext is null ? null : new
        {
            item.DependencyContext.Name, Target = Redaction.Scrub(item.DependencyContext.Target),
            item.DependencyContext.Success, item.DependencyContext.DurationMs
        },
        item.ResourceMetrics, Metadata = Sanitize(item.Metadata)
    };
    private static Dictionary<string, string>? Sanitize(Dictionary<string, string>? values) => values?
        .Take(100).ToDictionary(pair => Bound(pair.Key, 100)!, pair => Bound(Redaction.Scrub(pair.Value), 1000) ?? string.Empty);
}
