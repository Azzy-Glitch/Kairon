namespace Kairon.Backend.Models.Platform;

/// <summary>
/// A short-lived (10 minute) pairing code, hashed at rest, redeemed exactly once for a real
/// ProjectApiCredential. Adapted from Azzy's productization branch: that version anchored a
/// session to a MonitoredApplication (a modeled entity this codebase doesn't have); here it
/// anchors directly to Project, matching how every other telemetry path already scopes itself.
/// </summary>
public sealed class SdkPairingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string SdkType { get; set; } = string.Empty;
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }

    /// <summary>The ProjectApiCredential this session's redemption issued - set alongside
    /// RedeemedAt. A loose Guid reference (this codebase's dominant cross-entity convention), not
    /// a real FK: a credential's own lifecycle (revocation) is independent of the pairing session
    /// that originally minted it. Lets ConfirmAsync verify the SDK is proving possession of THIS
    /// exact credential, not merely any currently-valid one for the project.</summary>
    public Guid? IssuedCredentialId { get; set; }

    /// <summary>Set only once the SDK itself calls ConfirmAsync, authenticating with the exact
    /// issued credential - proof it was actually received and persisted, not merely that the
    /// backend's redeem response claimed to issue it. A re-pair completion (CompleteRepairAsync)
    /// refuses to revoke the credential being replaced until this is set.</summary>
    public DateTime? ConfirmedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>For a re-pair session, the exact credential this session was created to replace -
    /// recorded once, at creation, and never inferred or substituted later. CompleteRepairAsync
    /// requires the caller-supplied "old credential" to equal this exact value: without this
    /// binding, a pairing session could be completed against ANY active same-project credential
    /// the caller happened to name, letting an unrelated credential be revoked/replaced by a
    /// session that was never actually about it. Null for a first-time pairing session, which has
    /// no credential to replace.</summary>
    public Guid? ReplacesCredentialId { get; set; }

    /// <summary>Set only once CompleteRepairAsync has actually finished the rebind+revoke for this
    /// session - the authoritative "this exact operation already ran" signal. Distinct from simply
    /// noticing that the old credential is revoked: something else entirely (an unrelated,
    /// unexpected revocation) could also leave the old credential revoked without this session's
    /// own completion ever having run, and treating that as "success" would misreport that the
    /// intended rebind happened when it did not. Replaying CompleteRepairAsync after this is set is
    /// always a safe, side-effect-free idempotent no-op.</summary>
    public DateTime? CompletedAt { get; set; }
}
