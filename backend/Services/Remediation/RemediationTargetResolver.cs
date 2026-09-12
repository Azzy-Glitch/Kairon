using System.Text.RegularExpressions;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services.Remediation;

public enum DetectionTargetOutcome { NoTarget, Unique, Ambiguous }

/// <summary>Result of DetectionEngine-grade target resolution: ProjectId+Environment+Service
/// matching only - no credential, machine, or heartbeat validation, matching DetectionEngine's
/// original inline behavior exactly.</summary>
public readonly record struct DetectionTargetResolution(DetectionTargetOutcome Outcome, Guid? MachineId)
{
    public static readonly DetectionTargetResolution None = new(DetectionTargetOutcome.NoTarget, null);
    public static readonly DetectionTargetResolution AmbiguousResult = new(DetectionTargetOutcome.Ambiguous, null);
    public static DetectionTargetResolution UniqueTo(Guid machineId) => new(DetectionTargetOutcome.Unique, machineId);
}

/// <summary>Result of TelemetryController-grade target resolution: the exact bound credential a
/// machine-scoped telemetry request must be authenticated with.</summary>
public sealed record TelemetryTargetResolution(Guid TelemetryCredentialId, string CredentialKeyHash);

/// <summary>
/// Validated boundary for the AllowedOperationsJson column (Models/Platform/RemediationTarget.cs)
/// - the fixed, typed remediation registry (ServiceToolNames), never an arbitrary string. No
/// operation outside this set can ever be persisted or matched.
/// </summary>
public static class RemediationTargetOperations
{
    public static readonly IReadOnlyList<string> Known =
    [
        ServiceToolNames.RestartService,
        ServiceToolNames.StartService,
        ServiceToolNames.StopService,
        ServiceToolNames.RunHealthCheck
    ];

    public static bool IsKnown(string operation) => Known.Contains(operation, StringComparer.Ordinal);

    /// <summary>Drops unknown operations, de-duplicates, and orders deterministically (by the
    /// fixed Known order) so the persisted JSON never depends on caller-supplied ordering.</summary>
    public static string Serialize(IEnumerable<string> operations)
    {
        var normalized = operations.Where(IsKnown).Distinct(StringComparer.Ordinal)
            .OrderBy(o => Known.ToList().IndexOf(o)).ToList();
        return SreJson.Serialize(normalized);
    }

    public static List<string> Deserialize(string json) =>
        SreJson.Deserialize(json, new List<string>()).Where(IsKnown).ToList();
}

/// <summary>
/// Centralizes remediation-target resolution against the database-backed RemediationTarget
/// entity (Models/Platform/RemediationTarget.cs) - the persistent replacement for
/// WindowsRemediation:Targets (Configuration/WindowsRemediationOptions.cs). Each consumer gets
/// exactly the validation it performed inline before this existed:
///   - DetectionEngine: scope-only match, no credential/machine/heartbeat checks.
///   - TelemetryController: scope + machine-existence + credential-binding match.
///   - WindowsServiceTool: full execution-grade validation (the former private Target() method).
/// The database query and target-shape logic is written once; no consumer duplicates it, and no
/// consumer receives validation depth it does not need.
/// </summary>
public interface IRemediationTargetResolver
{
    Task<DetectionTargetResolution> ResolveDetectionTargetAsync(Guid projectId, string environment, string service, CancellationToken ct = default);

    Task<TelemetryTargetResolution?> ResolveTelemetryTargetAsync(Guid projectId, Guid machineId, string environment, string service, CancellationToken ct = default);

    Task<WindowsServiceTarget?> ResolveExecutionTargetAsync(Guid projectId, string environment, string service, string operation, CancellationToken ct = default);

    /// <summary>One-time, idempotent import of WindowsRemediation:Targets (appsettings.json) into
    /// the database. Only inserts a target for a (ProjectId, Environment, Service) key that has no
    /// existing database row yet - never overwrites an existing, possibly operator-modified, row.
    /// Safe to call on every startup: once a key exists in the database, that row - not
    /// configuration - is authoritative for it from then on.</summary>
    Task ImportLegacyConfigurationAsync(CancellationToken ct = default);
}

public sealed class RemediationTargetResolver : IRemediationTargetResolver
{
    private readonly AppDbContext _db;
    private readonly WindowsRemediationOptions _legacyOptions;

    public RemediationTargetResolver(AppDbContext db, IOptions<WindowsRemediationOptions> legacyOptions)
    {
        _db = db;
        _legacyOptions = legacyOptions.Value;
    }

    public async Task<DetectionTargetResolution> ResolveDetectionTargetAsync(Guid projectId, string environment, string service, CancellationToken ct = default)
    {
        var normalizedEnvironment = environment.ToLowerInvariant();
        var machineIds = await _db.RemediationTargets.AsNoTracking()
            .Where(t => t.Enabled && t.ProjectId == projectId && t.EnvironmentNormalized == normalizedEnvironment && t.Service == service)
            .Select(t => t.MachineId)
            .ToListAsync(ct);

        return machineIds.Count switch
        {
            0 => DetectionTargetResolution.None,
            1 => DetectionTargetResolution.UniqueTo(machineIds[0]),
            _ => DetectionTargetResolution.AmbiguousResult
        };
    }

    public async Task<TelemetryTargetResolution?> ResolveTelemetryTargetAsync(Guid projectId, Guid machineId, string environment, string service, CancellationToken ct = default)
    {
        var normalizedEnvironment = environment.ToLowerInvariant();
        var credentialIds = await _db.RemediationTargets.AsNoTracking()
            .Where(t => t.Enabled && t.ProjectId == projectId && t.MachineId == machineId &&
                        t.EnvironmentNormalized == normalizedEnvironment && t.Service == service)
            .Select(t => t.TelemetryCredentialId)
            .ToListAsync(ct);

        if (credentialIds.Count != 1 || !await _db.Machines.AnyAsync(m => m.Id == machineId, ct))
            return null;

        var credential = await _db.ProjectApiCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == credentialIds[0] && c.ProjectId == projectId && c.RevokedAt == null, ct);

        return credential is null ? null : new TelemetryTargetResolution(credential.Id, credential.KeyHash);
    }

    public async Task<WindowsServiceTarget?> ResolveExecutionTargetAsync(Guid projectId, string environment, string service, string operation, CancellationToken ct = default)
    {
        if (!ProductEnvironments.Contains(environment) || !await _db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct))
            return null;

        var normalizedEnvironment = environment.ToLowerInvariant();
        var entities = await _db.RemediationTargets.AsNoTracking()
            .Where(t => t.Enabled && t.ProjectId == projectId && t.EnvironmentNormalized == normalizedEnvironment && t.Service == service)
            .ToListAsync(ct);
        if (entities.Count != 1) return null;

        var entity = entities[0];
        if (!await _db.ProjectApiCredentials.AnyAsync(c => c.Id == entity.TelemetryCredentialId && c.ProjectId == projectId && c.RevokedAt == null, ct))
            return null;

        var target = ToRuntimeTarget(entity);
        if (!target.AllowedOperations.Contains(operation, StringComparer.Ordinal) || target.MachineId == Guid.Empty ||
            !Regex.IsMatch(target.ExpectedHostName, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") ||
            !Regex.IsMatch(target.WindowsServiceName, @"^[A-Za-z0-9_.-]{1,256}$"))
            return null;

        var machine = await _db.Machines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == target.MachineId, ct);
        if (machine is null || string.IsNullOrWhiteSpace(machine.AgentCredentialHash) ||
            !machine.HostName.Equals(target.ExpectedHostName, StringComparison.OrdinalIgnoreCase) ||
            !machine.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
            machine.LastSeenAt > DateTime.UtcNow.AddSeconds(5) ||
            machine.LastSeenAt < DateTime.UtcNow.AddSeconds(-Math.Clamp(_legacyOptions.MachineHeartbeatMaxAgeSeconds, 10, 300)))
            return null;

        // Bundled into the same result WindowsServiceTool.TargetFingerprint needs, rather than
        // making that caller re-query this exact Machine row a second time.
        target.AgentCredentialHash = machine.AgentCredentialHash;
        return target;
    }

    public async Task ImportLegacyConfigurationAsync(CancellationToken ct = default)
    {
        if (_legacyOptions.Targets.Count == 0) return;

        foreach (var legacy in _legacyOptions.Targets)
        {
            var normalizedEnvironment = legacy.Environment.ToLowerInvariant();
            var exists = await _db.RemediationTargets.AnyAsync(t =>
                t.ProjectId == legacy.ProjectId && t.EnvironmentNormalized == normalizedEnvironment && t.Service == legacy.Service, ct);
            if (exists) continue;

            _db.RemediationTargets.Add(new RemediationTarget
            {
                ProjectId = legacy.ProjectId,
                Environment = legacy.Environment,
                EnvironmentNormalized = normalizedEnvironment,
                Service = legacy.Service,
                MachineId = legacy.MachineId,
                TelemetryCredentialId = legacy.TelemetryCredentialId,
                ExpectedHostName = legacy.ExpectedHostName,
                WindowsServiceName = legacy.WindowsServiceName,
                AllowedOperationsJson = RemediationTargetOperations.Serialize(legacy.AllowedOperations),
                Enabled = true
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private static WindowsServiceTarget ToRuntimeTarget(RemediationTarget entity) => new()
    {
        ProjectId = entity.ProjectId,
        Environment = entity.Environment,
        Service = entity.Service,
        MachineId = entity.MachineId,
        TelemetryCredentialId = entity.TelemetryCredentialId,
        ExpectedHostName = entity.ExpectedHostName,
        WindowsServiceName = entity.WindowsServiceName,
        AllowedOperations = RemediationTargetOperations.Deserialize(entity.AllowedOperationsJson)
    };
}
