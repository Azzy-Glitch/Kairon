using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Controllers;

/// <summary>
/// Installation-scoped SDK credentials (docs/DESKTOP_SHELL.md) - an optional, stronger tier above
/// the existing project-level pairing in SdkPairingController, which is untouched. An operator
/// issues one of these directly against a known MonitoredApplication (mirrors
/// ProjectCredentialService's existing create-once pattern), rather than through a pairing-code
/// redemption.
/// </summary>
[ApiController]
[Route("api/v1/platform")]
[RequiresOperator]
public sealed class SdkInstallationsController : ControllerBase
{
    private readonly ISdkInstallationService _installations;
    private readonly IPlatformAuditService _audit;
    private readonly AppDbContext _db;

    public SdkInstallationsController(ISdkInstallationService installations, IPlatformAuditService audit, AppDbContext db)
    {
        _installations = installations;
        _audit = audit;
        _db = db;
    }

    [HttpGet("applications")]
    [RequiresOperator]
    public async Task<IActionResult> Applications([FromQuery] Guid? projectId, CancellationToken cancellationToken)
    {
        var query = _db.MonitoredApplications.AsNoTracking().AsQueryable();
        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId.Value);
        return Ok(await query.OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.ProjectId, x.Name, x.Service, x.Runtime, x.CreatedAt, x.LastTelemetryAt })
            .ToListAsync(cancellationToken));
    }

    [HttpPost("applications/{applicationId:guid}/installations")]
    [RequiresOperator]
    public async Task<IActionResult> Issue(Guid applicationId, [FromBody] IssueInstallationRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _installations.IssueAsync(applicationId, request.SdkType, request.Version, cancellationToken);
        if (created is null) return BadRequest(new { error = "Application not found or SDK type is invalid (use 'dotnet' or 'python')." });

        _audit.Record("sdk.installation-issued", Actor(), "sdk-installation", created.Id.ToString(), created.ProjectId,
            data: new { created.ApplicationId, created.InstallationId, request.SdkType });
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(created);
    }

    [HttpGet("sdk-installations")]
    [RequiresOperator]
    public async Task<IActionResult> Installations([FromQuery] Guid? applicationId, CancellationToken cancellationToken) =>
        Ok(await _installations.ListAsync(applicationId, cancellationToken));

    [HttpDelete("sdk-installations/{installationId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> Revoke(Guid installationId, CancellationToken cancellationToken)
    {
        var installation = await _db.SdkInstallations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation is null || !await _installations.RevokeAsync(installationId, cancellationToken)) return NotFound();

        _audit.Record("sdk.installation-revoked", Actor(), "sdk-installation", installationId.ToString(), installation.ProjectId);
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private string Actor() => Request.Headers["X-Kairon-Operator"].ToString() is { Length: > 0 } value
        ? value
        : "local-operator";
}

public sealed class IssueInstallationRequest
{
    public string SdkType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
