namespace Kairon.Backend.Models.Platform;

/// <summary>
/// Persistent, database-backed replacement for the WindowsRemediation:Targets configuration
/// section (backend/Configuration/WindowsRemediationOptions.cs - WindowsServiceTarget). Binds a
/// project/environment/service scope to exactly one enrolled machine and Windows service, the
/// exact project telemetry credential authorized to report on its behalf, and the deterministic
/// set of remediation operations allowed against it.
///
/// This is not merely an execution setting: TelemetryController's machine-scoped SDK
/// authorization (AuthorizeMachineScopeAsync) resolves the same
/// (ProjectId, MachineId, Environment, Service) -> TelemetryCredentialId mapping this entity
/// stores, so a change here can affect telemetry authorization as well as remediation.
///
/// AllowedOperations is stored as AllowedOperationsJson (this codebase's established convention
/// for a list-of-strings column - see SreIncident.TelemetryReferencesJson,
/// RemediationAction.ParametersJson) rather than an EF-native collection or a join table; see
/// Kairon.Backend.Services.Remediation.RemediationTargetOperations for the validated
/// serialize/deserialize boundary. Runtime consumers never read this raw JSON column directly.
/// </summary>
public sealed class RemediationTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Environment { get; set; } = "Production";
    public string Service { get; set; } = string.Empty;
    public Guid MachineId { get; set; }
    public Guid TelemetryCredentialId { get; set; }
    public string ExpectedHostName { get; set; } = string.Empty;
    public string WindowsServiceName { get; set; } = string.Empty;
    public string AllowedOperationsJson { get; set; } = "[]";

    /// <summary>Disabled targets are retained rather than deleted (audit/history, and so a
    /// duplicate ProjectId+Environment+Service pair can be resolved by disabling the stale one
    /// instead of losing it) but never match at runtime and do not count toward the
    /// enabled-uniqueness constraint - see AppDbContext's filtered unique index on this entity.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The actual EF concurrency token (AppDbContext) - regenerated to a fresh random
    /// value on every write that changes this row. UpdatedAt alone is not safe as a concurrency
    /// token: two independent writes can compute the identical wall-clock value (coarse OS clock
    /// resolution, or two requests landing in the same tick), which would make EF's "did the
    /// original value I read still match?" check pass even though someone else's write already
    /// landed in between - silently losing that update. A freshly-randomized Guid can never
    /// collide with the value it replaces, so it is safe as the sole concurrency token regardless
    /// of clock resolution. UpdatedAt itself remains for display and for the existing
    /// ExpectedUpdatedAt stale-form pre-check, which is a coarser, human-facing "was this edited
    /// since I loaded the form" signal - distinct from, and not a substitute for, this token.</summary>
    public Guid RowVersion { get; set; } = Guid.NewGuid();
}
