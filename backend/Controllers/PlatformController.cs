using AIDIP.Backend.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AIDIP.Backend.Services;
using AIDIP.Backend.Services.Audit;

namespace AIDIP.Backend.Controllers;

[ApiController]
[Route("api/v1/platform")]
[RequiresOperator]
public sealed class PlatformController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IProjectCredentialService _credentials;
    private readonly TimeProvider _time;
    private readonly IPlatformAuditService _audit;
    public PlatformController(AppDbContext db, IProjectCredentialService credentials, TimeProvider time,
        IPlatformAuditService audit)
    {
        _db = db;
        _credentials = credentials;
        _time = time;
        _audit = audit;
    }

    [HttpPost("projects")]
    [RequiresOperator]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectDto request,
        CancellationToken cancellationToken)
    {
        var slug = Slugify(request.Slug ?? request.Name);
        if (string.IsNullOrWhiteSpace(slug)) return BadRequest(new { error = "A valid project name or slug is required." });
        if (await _db.Projects.AnyAsync(x => x.Slug == slug, cancellationToken))
            return Conflict(new { error = "Project slug already exists." });
        var project = new AIDIP.Backend.Models.KaironProject { Id = request.Id ?? Guid.NewGuid(),
            Name = request.Name.Trim(), Slug = slug, CreatedAt = _time.GetUtcNow().UtcDateTime };
        _db.Projects.Add(project);
        _audit.Record("project.created", Actor(), "project", project.Id.ToString(), project.Id,
            data: new { project.Name, project.Slug });
        await _db.SaveChangesAsync(cancellationToken);
        return Created($"/api/v1/platform/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    [HttpGet("projects")]
    public async Task<IActionResult> Projects(CancellationToken cancellationToken) => Ok(await _db.Projects
        .AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Slug, x.IsActive, x.CreatedAt,
            Applications = x.Applications.Count, Environments = x.Environments.Count }).ToListAsync(cancellationToken));

    [HttpGet("applications")]
    public async Task<IActionResult> Applications([FromQuery] Guid? projectId, CancellationToken cancellationToken)
    {
        var query = _db.MonitoredApplications.AsNoTracking().AsQueryable();
        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId.Value);
        return Ok(await query.OrderBy(x => x.Name).Select(x => new { x.Id, x.ProjectId, x.Name, x.Service,
            x.Runtime, x.CreatedAt, x.LastTelemetryAt,
            Sources = x.TelemetrySources.Select(s => new { s.Id, s.SourceType, s.Version, s.LastSeenAt, s.IsActive })
        }).ToListAsync(cancellationToken));
    }

    [HttpPost("projects/{projectId:guid}/credentials")]
    [RequiresOperator]
    public async Task<IActionResult> CreateCredential(Guid projectId, [FromBody] CredentialNameDto request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var created = await _credentials.CreateAsync(projectId, request.Name, cancellationToken);
        if (created is null) return NotFound(new { error = "Project not found." });
        _audit.Record("credential.created", Actor(), "project-credential", created.Id.ToString(), projectId,
            data: new { created.Name, created.KeyPrefix });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(created);
    }

    [HttpDelete("projects/{projectId:guid}/credentials/{credentialId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> RevokeCredential(Guid projectId, Guid credentialId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await _credentials.RevokeAsync(projectId, credentialId, cancellationToken)) return NotFound();
        _audit.Record("credential.revoked", Actor(), "project-credential", credentialId.ToString(), projectId);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] Guid? projectId, [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = _db.PlatformAuditEvents.AsNoTracking().AsQueryable();
        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId);
        return Ok(await query.OrderByDescending(x => x.Timestamp).Take(Math.Clamp(limit, 1, 500))
            .Select(x => new { x.Id, x.Timestamp, x.Category, x.Action, x.Actor, x.TargetType, x.TargetId,
                x.ProjectId, x.Result, x.Message, x.DataJson }).ToListAsync(cancellationToken));
    }

    private string Actor() => Request.Headers["X-KAIRON-Operator"].ToString() is { Length: > 0 } actor
        ? actor
        : "local-operator";

    private static string Slugify(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant().Select(character =>
            char.IsLetterOrDigit(character) ? character : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-')[..Math.Min(slug.Trim('-').Length, 100)];
    }
}

public sealed class CredentialNameDto
{
    [System.ComponentModel.DataAnnotations.MaxLength(100)] public string Name { get; set; } = "Telemetry";
}

public sealed class CreateProjectDto
{
    public Guid? Id { get; set; }
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.MaxLength(100)] public string? Slug { get; set; }
}
