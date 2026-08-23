using AIDIP.Backend.DTOs;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;
using AIDIP.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IContextEngine _context;

    public TelemetryController(AppDbContext db, IContextEngine context)
    {
        _db = db;
        _context = context;
    }

    [HttpPost("incidents")]
    public async Task<IActionResult> CreateIncident(
        [FromBody] TelemetryPayload dto,
        CancellationToken cancellationToken)
    {
        var incident = new Incident
        {
            ProjectId = dto.ProjectId,
            Endpoint = dto.Endpoint,
            Method = dto.Method,
            StatusCode = dto.StatusCode,
            DurationMs = dto.Duration,
            ErrorMessage = dto.Error,
            Timestamp = dto.Timestamp
        };

        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            id = incident.Id,
            status = "recorded"
        });
    }

    [HttpPost("metrics")]
    public async Task<IActionResult> CreateMetric([FromBody] MetricDto dto)
    {
        var metric = new Metric
        {
            ProjectId = dto.ProjectId,
            CpuPercent = dto.CpuPercent,
            MemoryPercent = dto.MemoryPercent,
            ResponseTimeMs = dto.ResponseTimeMs,
            Timestamp = DateTime.UtcNow
        };

        _db.Metrics.Add(metric);
        await _db.SaveChangesAsync();
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
}
