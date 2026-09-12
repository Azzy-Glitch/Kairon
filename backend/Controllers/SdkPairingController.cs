using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kairon.Backend.Controllers;

/// <summary>
/// The real pairing flow: an operator mints a temporary code for a project; a developer's SDK
/// redeems it once, unattended, for a persistent credential. Adapted from Azzy's productization
/// branch onto this codebase's simpler Project-only model (docs/DESKTOP_SHELL.md).
///
/// Every mutating call here delegates its audit recording to ISdkPairingService itself (see that
/// interface's remarks) - the service records the audit event and persists it in the SAME
/// SaveChangesAsync/transaction as the business-state change it describes, so this controller
/// never needs its own separate, later save that could lose the audit trail for a mutation that
/// had already taken effect.
/// </summary>
[ApiController]
public sealed class SdkPairingController : ControllerBase
{
    private readonly ISdkPairingService _pairing;

    public SdkPairingController(ISdkPairingService pairing) => _pairing = pairing;

    [HttpPost("api/v1/projects/{projectId:guid}/pairing")]
    [RequiresOperator]
    public async Task<IActionResult> Create(Guid projectId, [FromBody] PairingRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _pairing.CreateAsync(projectId, request.SdkType, cancellationToken,
            request.ReplacesCredentialId, Actor());
        if (created is null)
            return BadRequest(new
            {
                error = "Project not found or inactive, SDK type is invalid (use 'dotnet' or 'python'), or the " +
                         "credential to replace does not exist, belongs to another project, or is already revoked."
            });

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

        return Ok(paired);
    }

    [HttpDelete("api/v1/platform/pairing/{pairingId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> RevokePairing(Guid pairingId, CancellationToken cancellationToken)
    {
        if (!await _pairing.RevokePairingAsync(pairingId, cancellationToken, Actor())) return NotFound();
        return NoContent();
    }

    /// <summary>Lets the operator UI poll a pairing session (including a re-pair session, which is
    /// just another pairing session for an already-connected project) until it is Redeemed, Expired
    /// or Cancelled - never returns the code, its hash, or any issued credential secret.</summary>
    [HttpGet("api/v1/platform/pairing/{pairingId:guid}")]
    [RequiresOperator]
    public async Task<IActionResult> GetStatus(Guid pairingId, CancellationToken cancellationToken)
    {
        var status = await _pairing.GetStatusAsync(pairingId, cancellationToken);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>Called by the SDK itself, unattended, immediately after it durably persists the
    /// credential redemption just issued - the only trustworthy proof that the redeem response was
    /// actually received and saved, not merely that the backend issued it. Authenticates by
    /// requiring the exact api key this session issued; never accepts a bare claim. Safe to call
    /// more than once (recoverable if a previous confirmation's response was lost).</summary>
    [HttpPost("api/v1/sdk/pair/{pairingId:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid pairingId, [FromBody] ConfirmPairingRequest request, CancellationToken cancellationToken)
    {
        if (!await _pairing.ConfirmAsync(pairingId, request.ApiKey, cancellationToken))
            return BadRequest(new { error = "Pairing session not found, not yet redeemed, or the supplied API key does not match." });
        return NoContent();
    }

    /// <summary>Operator-driven re-pair completion: only proceeds once the SDK has itself Confirmed
    /// the new credential (never on Redeemed alone - see SdkPairingService's remarks), and only for
    /// the exact old credential this session was bound to replace at creation. Atomically rebinds
    /// every enabled RemediationTarget bound to oldCredentialId - scoped to this session's own
    /// project - onto the newly issued credential, then revokes oldCredentialId.</summary>
    [HttpPost("api/v1/platform/pairing/{pairingId:guid}/complete-repair")]
    [RequiresOperator]
    public async Task<IActionResult> CompleteRepair(Guid pairingId, [FromBody] CompleteRepairRequest request, CancellationToken cancellationToken)
    {
        var result = await _pairing.CompleteRepairAsync(pairingId, request.OldCredentialId, cancellationToken, Actor());
        return result.Outcome switch
        {
            CompleteRepairOutcome.Success => Ok(new { rebindCount = result.RebindCount }),
            CompleteRepairOutcome.NotConfirmed => Conflict(new
            {
                error = "The new credential has not been confirmed by the application yet. Wait for confirmation before completing the re-pair."
            }),
            CompleteRepairOutcome.OldCredentialMismatch => BadRequest(new
            {
                error = "That credential is not the one this pairing session was created to replace."
            }),
            CompleteRepairOutcome.OldCredentialWrongProject => BadRequest(new
            {
                error = "That credential does not belong to this pairing session's project."
            }),
            CompleteRepairOutcome.OldCredentialNotFound => NotFound(new { error = "The credential to be replaced was not found." }),
            CompleteRepairOutcome.OldCredentialAlreadyRevoked => Conflict(new
            {
                error = "The credential to be replaced has already been revoked by something other than this re-pair. Nothing was changed."
            }),
            CompleteRepairOutcome.NewCredentialRevoked => Conflict(new
            {
                error = "The newly issued credential has since been revoked and can no longer be completed onto. The old credential was left untouched."
            }),
            CompleteRepairOutcome.ConcurrentReplacementConflict => Conflict(new
            {
                error = "Another confirmed session already replaced this credential first. Nothing from this request was changed - reload the current status."
            }),
            _ => NotFound(new { error = "Pairing session not found." })
        };
    }

    private string Actor() => Request.Headers["X-Kairon-Operator"].ToString() is { Length: > 0 } value
        ? value
        : "local-operator";
}

public sealed class PairingRequest
{
    public string SdkType { get; set; } = string.Empty;

    /// <summary>For a re-pair: the exact credential this session is meant to replace, bound at
    /// creation time. Omitted (null) for a first-time pairing session, which has no credential to
    /// replace yet.</summary>
    public Guid? ReplacesCredentialId { get; set; }
}

public sealed class RedeemPairingRequest
{
    public string Code { get; set; } = string.Empty;
    public string SdkType { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public sealed class ConfirmPairingRequest
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class CompleteRepairRequest
{
    public Guid OldCredentialId { get; set; }
}
