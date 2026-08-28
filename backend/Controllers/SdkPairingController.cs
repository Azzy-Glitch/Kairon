using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Services;
using AIDIP.Backend.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Controllers;

[ApiController]
public sealed class SdkPairingController : ControllerBase
{
    private readonly ISdkPairingService _pairing;
    private readonly IPlatformAuditService _audit;
    private readonly AppDbContext _db;

    public SdkPairingController(ISdkPairingService pairing, IPlatformAuditService audit, AppDbContext db)
    {
        _pairing = pairing;
        _audit = audit;
        _db = db;
    }

    [HttpPost("api/v1/platform/applications/{applicationId:guid}/pairing")]
    [RequiresOperator]
    public async Task<IActionResult> Create(Guid applicationId, PairingRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _pairing.CreateAsync(applicationId, request.SdkType, cancellationToken);
        if (created is null) return BadRequest(new { error = "Application or SDK type is invalid." });
        var projectId = await _db.MonitoredApplications.Where(x => x.Id == applicationId)
            .Select(x => x.ProjectId).SingleAsync(cancellationToken);
        _audit.Record("sdk.pairing-created", Actor(), "application", applicationId.ToString(), projectId,
            data: new { created.PairingId, created.SdkType, created.ExpiresAt });
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(created);
    }

    [HttpPost("api/v1/sdk/pair")]
    public async Task<IActionResult> Pair(RedeemPairingRequest request, CancellationToken cancellationToken)
    {
        var paired = await _pairing.RedeemAsync(request.Code, request.SdkType, request.Version, cancellationToken);
        if (paired is null) return BadRequest(new { error = "Pairing code is invalid, expired, revoked, or already used." });
        _audit.Record("sdk.paired", "sdk:" + request.SdkType, "sdk-installation",
            paired.InstallationRecordId.ToString(), paired.ProjectId,
            data: new { paired.ApplicationId, paired.InstallationId, request.SdkType, request.Version });
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(paired);
    }

    [HttpDelete("api/v1/platform/pairing/{pairingId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> RevokePairing(Guid pairingId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == pairingId,
            cancellationToken);
        if (session is null || !await _pairing.RevokePairingAsync(pairingId, cancellationToken)) return NotFound();
        _audit.Record("sdk.pairing-revoked", Actor(), "sdk-pairing", pairingId.ToString(), session.ProjectId);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("api/v1/platform/sdk-installations")]
    [RequiresOperator]
    public async Task<IActionResult> Installations([FromQuery] Guid? applicationId,
        CancellationToken cancellationToken)
    {
        var query = _db.SdkInstallations.AsNoTracking().AsQueryable();
        if (applicationId.HasValue) query = query.Where(x => x.ApplicationId == applicationId.Value);
        return Ok(await query.OrderByDescending(x => x.CreatedAt).Select(x => new { x.Id, x.ProjectId,
            x.ApplicationId, x.SdkType, x.Version, x.InstallationId, x.CreatedAt, x.LastSeenAt,
            Connected = x.RevokedAt == null, x.RevokedAt }).ToListAsync(cancellationToken));
    }

    [HttpDelete("api/v1/platform/sdk-installations/{installationId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> Revoke(Guid installationId, CancellationToken cancellationToken)
    {
        var installation = await _db.SdkInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installationId,
            cancellationToken);
        if (installation is null || !await _pairing.RevokeAsync(installationId, cancellationToken)) return NotFound();
        _audit.Record("sdk.revoked", Actor(), "sdk-installation", installationId.ToString(), installation.ProjectId);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private string Actor() => Request.Headers["X-KAIRON-Operator"].ToString() is { Length: > 0 } value
        ? value : "local-operator";
}

public sealed class PairingRequest
{
    public string SdkType { get; set; } = string.Empty;
}

public sealed class RedeemPairingRequest
{
    public string Code { get; set; } = string.Empty;
    public string SdkType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
