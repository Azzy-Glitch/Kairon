using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Controllers;

/// <summary>
/// The real pairing flow: an operator mints a temporary code for a project; a developer's SDK
/// redeems it once, unattended, for a persistent credential. Adapted from Azzy's productization
/// branch onto this codebase's simpler Project-only model (docs/DESKTOP_SHELL.md).
/// </summary>
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

    [HttpPost("api/v1/projects/{projectId:guid}/pairing")]
    [RequiresOperator]
    public async Task<IActionResult> Create(Guid projectId, [FromBody] PairingRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _pairing.CreateAsync(projectId, request.SdkType, cancellationToken);
        if (created is null) return BadRequest(new { error = "Project not found or SDK type is invalid (use 'dotnet' or 'python')." });

        _audit.Record("sdk.pairing-created", Actor(), "project", projectId.ToString(), projectId,
            data: new { created.PairingId, created.SdkType, created.ExpiresAt });
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(created);
    }

    /// <summary>Called by an SDK, unattended - no operator key. The pairing code itself is the
    /// one-time proof of intent; it is single-use and expires in 10 minutes.</summary>
    [HttpPost("api/v1/sdk/pair")]
    public async Task<IActionResult> Pair([FromBody] RedeemPairingRequest request, CancellationToken cancellationToken)
    {
        var paired = await _pairing.RedeemAsync(request.Code, request.SdkType, request.Version, cancellationToken);
        if (paired is null)
            return BadRequest(new { error = "Pairing code is invalid, expired, revoked, or already used." });

        _audit.Record("sdk.paired", "sdk:" + request.SdkType, "project", paired.ProjectId.ToString(), paired.ProjectId,
            data: new { request.SdkType, request.Version });
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(paired);
    }

    [HttpDelete("api/v1/platform/pairing/{pairingId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> RevokePairing(Guid pairingId, CancellationToken cancellationToken)
    {
        var session = await _db.SdkPairingSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == pairingId, cancellationToken);
        if (session is null || !await _pairing.RevokePairingAsync(pairingId, cancellationToken)) return NotFound();

        _audit.Record("sdk.pairing-revoked", Actor(), "sdk-pairing", pairingId.ToString(), session.ProjectId);
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private string Actor() => Request.Headers["X-Kairon-Operator"].ToString() is { Length: > 0 } value
        ? value
        : "local-operator";
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
