using AIDIP.Backend.DTOs;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;
using AIDIP.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;

namespace AIDIP.Backend.Controllers;

[ApiController]
[Route("api/telemetry")]
[EnableRateLimiting("telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPlatformTelemetryService _platformTelemetry;
    private readonly IProjectCredentialService _credentials;
    private readonly AIDIP.Backend.Configuration.PlatformSecurityOptions _security;
    private readonly ISdkPairingService _pairing;

    public TelemetryController(
        AppDbContext db,
        IPlatformTelemetryService platformTelemetry, IProjectCredentialService credentials,
        Microsoft.Extensions.Options.IOptions<AIDIP.Backend.Configuration.PlatformSecurityOptions> security,
        ISdkPairingService pairing)
    {
        _db = db;
        _platformTelemetry = platformTelemetry;
        _credentials = credentials;
        _security = security.Value;
        _pairing = pairing;
    }

    [HttpPost("incidents")]
    public async Task<IActionResult> CreateIncident(
        [FromBody] TelemetryPayload dto,
        CancellationToken cancellationToken)
    {
        var application = string.IsNullOrWhiteSpace(dto.ApplicationName) ? "Unknown" : dto.ApplicationName;
        var service = dto.Service ?? application;
        if (!await IsAuthorized(dto.ProjectId, service, cancellationToken)) return Unauthorized();
        var eventId = Guid.NewGuid();
        var result = await _platformTelemetry.IngestAsync(new NormalizedTelemetryBatchDto { Events = [new()
        {
            EventId = eventId, ProjectId = dto.ProjectId, Timestamp = dto.Timestamp,
            EventType = dto.ExceptionType is null ? "http" : "exception", Severity = "Error",
            Source = "legacy-dotnet-sdk", Application = application,
            Service = dto.Service ?? application, Environment = dto.Environment,
            Message = dto.Error, ExceptionType = dto.ExceptionType, StackTrace = dto.StackTrace,
            HttpContext = new HttpTelemetryContextDto { Endpoint = dto.Endpoint, Method = dto.Method,
                StatusCode = dto.StatusCode, DurationMs = dto.Duration }
        }] }, cancellationToken);
        if (result.Accepted == 0) return BadRequest(new { error = "Telemetry payload is invalid." });

        // Keep this response compatible with AIDIP.SDK.Models.TelemetryResponse
        // without introducing a backend-to-SDK project dependency.
        return Ok(new
        {
            success = true,
            message = "Telemetry recorded.",
            telemetryId = eventId.ToString()
        });
    }

    [HttpPost("metrics")]
    public async Task<IActionResult> CreateMetric(
        [FromBody] MetricDto dto,
        CancellationToken cancellationToken)
    {
        var service = dto.Service ?? dto.Application ?? "Unknown";
        if (!await IsAuthorized(dto.ProjectId, service, cancellationToken)) return Unauthorized();
        var result = await _platformTelemetry.IngestAsync(new NormalizedTelemetryBatchDto { Events = [new()
        {
            EventId = Guid.NewGuid(), ProjectId = dto.ProjectId, Timestamp = dto.Timestamp,
            EventType = "resource_metric", Severity = "Information", Source = "legacy-dotnet-sdk",
            Application = dto.Application ?? dto.Service ?? "Unknown", Service = dto.Service ?? dto.Application ?? "Unknown",
            Environment = dto.Environment, Host = dto.Component ?? string.Empty,
            ResourceMetrics = new ResourceTelemetryMetricsDto { CpuPercent = dto.CpuPercent,
                MemoryPercent = dto.MemoryPercent, ResponseTimeMs = dto.ResponseTimeMs,
                RequestCount = dto.RequestCount, ErrorCount = dto.ErrorCount,
                RetryCount = dto.RetryCount, QueueDepth = dto.QueueDepth }
        }] }, cancellationToken);
        if (result.Accepted == 0) return BadRequest(new { error = "Metric payload is invalid." });

        return Ok(new { status = "recorded" });
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
    public async Task<IActionResult> GetMetrics([FromQuery] string? projectId, CancellationToken ct)
    {
        var q = _db.Metrics.AsQueryable();

        if (!string.IsNullOrEmpty(projectId))
        {
            if (!Guid.TryParse(projectId, out var pid))
                return BadRequest(new { error = "Invalid projectId format" });

            q = q.Where(m => m.ProjectId == pid);
        }

        var result = await q
            .OrderByDescending(m => m.Timestamp)
            .Take(20)
            .ToListAsync(ct);

        return Ok(result);
    }

    private async Task<bool> IsAuthorized(Guid projectId, string service, CancellationToken cancellationToken)
    {
        var key = TelemetryKey();
        return await _credentials.AuthorizeAsync([projectId], key, cancellationToken)
            || await _pairing.AuthorizeLegacyAsync(projectId, service, key, cancellationToken);
    }

    private string TelemetryKey()
    {
        var current = Request.Headers[_security.TelemetryKeyHeader].ToString();
        // Compatibility for AIDIP.SDK 1.0 clients. New KAIRON SDK versions use the configured
        // X-KAIRON header; accepting the legacy name avoids a breaking security rollout.
        return string.IsNullOrWhiteSpace(current) ? Request.Headers["X-AIDIP-API-Key"].ToString() : current;
    }
}
