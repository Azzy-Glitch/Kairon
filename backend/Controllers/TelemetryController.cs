using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Orchestration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IContextEngine _context;
    private readonly IIncidentProcessingQueue _queue;
    private readonly IProjectCredentialService _credentials;
    private readonly PlatformSecurityOptions _security;

    public TelemetryController(
        AppDbContext db,
        IContextEngine context,
        IIncidentProcessingQueue queue,
        IProjectCredentialService credentials,
        IOptions<PlatformSecurityOptions> security)
    {
        _db = db;
        _context = context;
        _queue = queue;
        _credentials = credentials;
        _security = security.Value;
    }

    /// <summary>
    /// Checks the per-project credential (docs/DESKTOP_SHELL.md) when
    /// PlatformSecurity:RequireTelemetryKey is on. Off by default, and a project with no issued
    /// credential keeps working unauthenticated even when it's on - this mirrors
    /// OperatorAuthorizationFilter's fail-open-until-configured stance, and is what keeps every
    /// existing test and the demo scenario passing unmodified.
    /// </summary>
    private async Task<bool> AuthorizeAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _credentials.AuthorizeAsync(projectId, Request.Headers[_security.TelemetryKeyHeader], cancellationToken);

    [HttpPost("incidents")]
    public async Task<IActionResult> CreateIncident(
        [FromBody] TelemetryPayload dto,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(dto.ProjectId, cancellationToken))
            return Unauthorized(new { error = "A valid project API key is required." });

        var incident = new Incident
        {
            ProjectId = dto.ProjectId,
            Endpoint = dto.Endpoint,
            Method = dto.Method,
            StatusCode = dto.StatusCode,
            DurationMs = dto.Duration,
            ErrorMessage = dto.Error,
            ErrorType = dto.ExceptionType,
            StackTrace = dto.StackTrace,
            Environment = string.IsNullOrWhiteSpace(dto.Environment) ? "Development" : dto.Environment,
            Timestamp = dto.Timestamp == default ? DateTime.UtcNow : dto.Timestamp,
            // The SDK has always sent ApplicationName; persisting it (and the service name) is what
            // lets detection and correlation attribute this row to a service.
            Application = string.IsNullOrWhiteSpace(dto.ApplicationName) ? null : dto.ApplicationName,
            Service = string.IsNullOrWhiteSpace(dto.Service) ? dto.ApplicationName : dto.Service
        };

        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(cancellationToken);

        // Detection runs on a background worker. Ingestion returns as soon as the row is durable;
        // it never waits for correlation or for an AI call (PRD section 6).
        _queue.TryEnqueue(new IncidentWorkItem(
            WorkItemKind.EvaluateDetection, incident.ProjectId, incident.Environment, incident.Service));

        // Keep this response compatible with Kairon.SDK.Models.TelemetryResponse
        // without introducing a backend-to-SDK project dependency.
        return Ok(new
        {
            success = true,
            message = "Telemetry recorded.",
            telemetryId = incident.Id.ToString()
        });
    }

    [HttpPost("metrics")]
    public async Task<IActionResult> CreateMetric(
        [FromBody] MetricDto dto,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(dto.ProjectId, cancellationToken))
            return Unauthorized(new { error = "A valid project API key is required." });

        var metric = new Metric
        {
            ProjectId = dto.ProjectId,
            CpuPercent = dto.CpuPercent,
            MemoryPercent = dto.MemoryPercent,
            ResponseTimeMs = dto.ResponseTimeMs,
            RequestCount = dto.RequestCount,
            ErrorCount = dto.ErrorCount,
            RetryCount = dto.RetryCount,
            QueueDepth = dto.QueueDepth,
            Application = dto.Application,
            Service = dto.Service,
            Component = dto.Component,
            Environment = string.IsNullOrWhiteSpace(dto.Environment) ? "Development" : dto.Environment,
            Timestamp = dto.Timestamp == default ? DateTime.UtcNow : dto.Timestamp
        };

        _db.Metrics.Add(metric);
        await _db.SaveChangesAsync(cancellationToken);

        _queue.TryEnqueue(new IncidentWorkItem(
            WorkItemKind.EvaluateDetection, metric.ProjectId, metric.Environment, metric.Service));

        return Ok(new { status = "recorded" });
    }

    /// <summary>
    /// Ingests a normalized event from the KAIRON Agent - a log pattern match or a process
    /// lifecycle/resource event (docs/OBSERVABILITY_MIGRATION.md). This is the one ingestion
    /// path that carries raw, free-text content the reporter did not necessarily redact itself
    /// (a log line can contain anything), so - unlike CreateIncident/CreateMetric, whose fields
    /// are already structured - the message is scrubbed here before it is ever persisted.
    /// </summary>
    [HttpPost("events")]
    public async Task<IActionResult> CreateEvent(
        [FromBody] AgentEventDto dto,
        CancellationToken cancellationToken)
    {
        if (!await AuthorizeAsync(dto.ProjectId, cancellationToken))
            return Unauthorized(new { error = "A valid project API key is required." });

        var agentEvent = new AgentEvent
        {
            ProjectId = dto.ProjectId,
            Timestamp = dto.Timestamp == default ? DateTime.UtcNow : dto.Timestamp,
            EventType = dto.EventType,
            Environment = string.IsNullOrWhiteSpace(dto.Environment) ? "Development" : dto.Environment,
            Application = dto.Application,
            Service = dto.Service,
            Component = dto.Component,
            Severity = string.IsNullOrWhiteSpace(dto.Severity) ? "Info" : dto.Severity,
            Message = Redaction.Scrub(dto.Message) ?? string.Empty,
            Source = dto.Source,
            OccurrenceCount = Math.Max(1, dto.OccurrenceCount),
            MetadataJson = dto.MetadataJson
        };

        _db.AgentEvents.Add(agentEvent);
        await _db.SaveChangesAsync(cancellationToken);

        // Same detection-evaluation trigger CreateIncident/CreateMetric already use - no new
        // work item kind needed, the sweep and this enqueue both just ask detection to look
        // again for this project/environment/service.
        _queue.TryEnqueue(new IncidentWorkItem(
            WorkItemKind.EvaluateDetection, agentEvent.ProjectId, agentEvent.Environment, agentEvent.Service));

        return Ok(new { success = true, message = "Event recorded.", eventId = agentEvent.Id.ToString() });
    }

    [HttpGet("incidents")]
    public async Task<IActionResult> GetIncidents([FromQuery] string? projectId, CancellationToken ct)
    {
        var q = _db.Incidents.AsQueryable();

        if (!string.IsNullOrEmpty(projectId))
        {
            if (!Guid.TryParse(projectId, out var pid))
                return BadRequest(new { error = "Invalid projectId format" });

            q = q.Where(i => i.ProjectId == pid);
        }

        var result = await q
            .OrderByDescending(i => i.Timestamp)
            .Take(50)
            .ToListAsync(ct);

        return Ok(result);
    }

    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetrics(
        [FromQuery] string? projectId,
        [FromQuery] string? service,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var q = _db.Metrics.AsQueryable();

        if (!string.IsNullOrEmpty(projectId))
        {
            if (!Guid.TryParse(projectId, out var pid))
                return BadRequest(new { error = "Invalid projectId format" });

            q = q.Where(m => m.ProjectId == pid);
        }

        // Optional: scope to one service, so a per-service trend (e.g. the Services page) does not
        // have to filter a mixed-service result client-side.
        if (!string.IsNullOrEmpty(service))
            q = q.Where(m => m.Service == service);

        var take = Math.Clamp(limit ?? 20, 1, 100);

        // Newest-first, unchanged from the existing behaviour the Telemetry Monitor screen already
        // depends on. A consumer that wants chronological order (e.g. a sparkline) reverses client-side.
        var result = await q
            .OrderByDescending(m => m.Timestamp)
            .Take(take)
            .ToListAsync(ct);

        return Ok(result);
    }
}
