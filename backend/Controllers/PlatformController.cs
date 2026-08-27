using AIDIP.Backend.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AIDIP.Backend.Services;

namespace AIDIP.Backend.Controllers;

[ApiController]
[Route("api/v1/platform")]
public sealed class PlatformController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IProjectCredentialService _credentials;
    private readonly TimeProvider _time;
    public PlatformController(AppDbContext db, IProjectCredentialService credentials, TimeProvider time)
    {
        _db = db;
        _credentials = credentials;
        _time = time;
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
        var created = await _credentials.CreateAsync(projectId, request.Name, cancellationToken);
        return created is null ? NotFound(new { error = "Project not found." }) : Ok(created);
    }

    [HttpDelete("projects/{projectId:guid}/credentials/{credentialId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> RevokeCredential(Guid projectId, Guid credentialId,
        CancellationToken cancellationToken) =>
        await _credentials.RevokeAsync(projectId, credentialId, cancellationToken) ? NoContent() : NotFound();

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
