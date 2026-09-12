using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Controllers;

/// <summary>
/// Project + per-project credential management - the anchor SDK pairing (SdkPairingController)
/// attaches to. Every write here changes something security-relevant, so it requires an operator
/// key when SreSecurity:RequireOperatorKey is on (matches the existing remediation-approval
/// gate), reusing [RequiresOperator]/OperatorAuthorizationFilter rather than a new auth scheme.
/// </summary>
[ApiController]
[Route("api/v1/projects")]
[RequiresOperator]
public sealed class ProjectsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IProjectCredentialService _credentials;
    private readonly IPlatformAuditService _audit;
    private readonly TimeProvider _time;

    public ProjectsController(AppDbContext db, IProjectCredentialService credentials, IPlatformAuditService audit,
        TimeProvider time)
    {
        _db = db;
        _credentials = credentials;
        _audit = audit;
        _time = time;
    }

    [HttpPost]
    [RequiresOperator]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "A project name is required." });

        var project = new Project { Id = Guid.NewGuid(), Name = request.Name.Trim(), CreatedAt = _time.GetUtcNow().UtcDateTime };
        _db.Projects.Add(project);
        _audit.Record("project.created", Actor(), "project", project.Id.ToString(), project.Id, data: new { project.Name });
        await _db.SaveChangesAsync(cancellationToken);

        return Created($"/api/v1/projects/{project.Id}", new { project.Id, project.Name, project.CreatedAt });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await _db.Projects
        .AsNoTracking()
        .OrderBy(x => x.Name)
        .Select(x => new
        {
            x.Id,
            x.Name,
            x.CreatedAt,
            ActiveCredentials = _db.ProjectApiCredentials.Count(c => c.ProjectId == x.Id && c.RevokedAt == null)
        })
        .ToListAsync(cancellationToken));

    [HttpPost("{projectId:guid}/credentials")]
    [RequiresOperator]
    public async Task<IActionResult> CreateCredential(Guid projectId, [FromBody] CreateCredentialRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _credentials.CreateAsync(projectId, request.Name, cancellationToken);
        if (created is null) return NotFound(new { error = "Project not found." });

        _audit.Record("credential.created", Actor(), "credential", created.Id.ToString(), projectId,
            data: new { created.Name, created.KeyPrefix });
        await _db.SaveChangesAsync(cancellationToken);

        // The raw key is returned exactly once, here - only the prefix+hash are ever persisted.
        return Ok(new { created.Id, created.Name, created.KeyPrefix, ApiKey = created.ApiKey, created.CreatedAt });
    }

    [HttpGet("{projectId:guid}/credentials")]
    [RequiresOperator]
    public async Task<IActionResult> ListCredentials(Guid projectId, CancellationToken cancellationToken) => Ok(
        await _db.ProjectApiCredentials.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Name, x.KeyPrefix, x.CreatedAt, x.RevokedAt })
            .ToListAsync(cancellationToken));

    [HttpDelete("{projectId:guid}/credentials/{credentialId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> RevokeCredential(Guid projectId, Guid credentialId, CancellationToken cancellationToken)
    {
        var outcome = await _credentials.RevokeAsync(projectId, credentialId, cancellationToken);
        switch (outcome)
        {
            case RevokeCredentialOutcome.NotFound:
                return NotFound();
            case RevokeCredentialOutcome.ConcurrentConflict:
                return Conflict(new
                {
                    error = "This credential was modified by something else at the same moment - reload and try again."
                });
        }
        _audit.Record("credential.revoked", Actor(), "credential", credentialId.ToString(), projectId);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private string Actor() => Request.Headers["X-Kairon-Operator"].ToString() is { Length: > 0 } value
        ? value
        : "local-operator";
}

public sealed class CreateProjectRequest
{
    public string Name { get; set; } = string.Empty;
}

public sealed class CreateCredentialRequest
{
    public string Name { get; set; } = string.Empty;
}
