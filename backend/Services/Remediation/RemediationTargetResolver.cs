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

/// <summary>Why a remediation target can or cannot execute right now. Every value except
/// <see cref="Ready"/> fails closed; the classification exists so operators see the actual reason
/// (pre-flight, target status, execution failure) instead of a generic refusal.</summary>
public enum TargetReadiness
{
    Ready,
    TargetMissing,
    TargetDisabled,
    AmbiguousTarget,
    ProjectInactive,
    CredentialInvalid,
    OperationNotAllowed,
    InvalidTarget,
    RemoteNotSupported,
    MachineMismatch,
    MachineOffline,
    AwaitingAgentConfirmation,
    StaleTarget,
    ServiceMissing,
    Denylisted,
    PermissionMissing,
    ServiceIdentityChanged,
    UnsupportedPlatform,
    /// <summary>AppProcess: the Agent has not yet identified which process sends the app's
    /// telemetry, or the SDK has not reported its working directory (older Agent or SDK).</summary>
    ProcessUnknown,
    /// <summary>AppProcess: no KAIRON UserAgent in the app owner's signed-in session currently
    /// reports that process, so nothing could restart it.</summary>
    UserAgentOffline
}

public sealed record TargetEvaluation(WindowsServiceTarget? Target, TargetReadiness Readiness)
{
    public static TargetEvaluation Fail(TargetReadiness readiness) => new(null, readiness);
}

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
        ServiceToolNames.RunHealthCheck,
        ServiceToolNames.RestartApplication
    ];

    /// <summary>The operations each target kind can carry. A Windows-service target never
    /// restarts an application process and an application-process target never touches the SCM.</summary>
    public static IReadOnlyList<string> ForKind(string kind) => kind == RemediationTargetKinds.AppProcess
        ? [ServiceToolNames.RestartApplication]
        : [ServiceToolNames.RestartService, ServiceToolNames.StartService, ServiceToolNames.StopService, ServiceToolNames.RunHealthCheck];

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

    /// <summary>Execution-grade evaluation of one persisted target with the precise refusal reason.
    /// A null operation checks the target without a specific operation (status views);
    /// ResolveExecutionTargetAsync is exactly this plus the scope lookup.</summary>
    Task<TargetEvaluation> EvaluateTargetAsync(RemediationTarget entity, string? operation, CancellationToken ct = default);

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
    private readonly ILocalMachine _localMachine;

    public RemediationTargetResolver(AppDbContext db, IOptions<WindowsRemediationOptions> legacyOptions, ILocalMachine? localMachine = null)
    {
        _db = db;
        _legacyOptions = legacyOptions.Value;
        _localMachine = localMachine ?? LocalMachine.Instance;
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
        if (!ProductEnvironments.Contains(environment)) return null;
        var normalizedEnvironment = environment.ToLowerInvariant();
        var entities = await _db.RemediationTargets.AsNoTracking()
            .Where(t => t.Enabled && t.ProjectId == projectId && t.EnvironmentNormalized == normalizedEnvironment && t.Service == service)
            .ToListAsync(ct);
        if (entities.Count != 1) return null;
        return (await EvaluateTargetAsync(entities[0], operation, ct)).Target;
    }

    public async Task<TargetEvaluation> EvaluateTargetAsync(RemediationTarget entity, string? operation, CancellationToken ct = default)
    {
        if (!entity.Enabled) return TargetEvaluation.Fail(TargetReadiness.TargetDisabled);
        if (!ProductEnvironments.Contains(entity.Environment) || !await _db.Projects.AnyAsync(p => p.Id == entity.ProjectId && p.IsActive, ct))
            return TargetEvaluation.Fail(TargetReadiness.ProjectInactive);
        if (!await _db.ProjectApiCredentials.AnyAsync(c => c.Id == entity.TelemetryCredentialId && c.ProjectId == entity.ProjectId && c.RevokedAt == null, ct))
            return TargetEvaluation.Fail(TargetReadiness.CredentialInvalid);

        var target = ToRuntimeTarget(entity);
        var isAppProcess = target.Kind == RemediationTargetKinds.AppProcess;
        if (operation is not null && (!target.AllowedOperations.Contains(operation, StringComparer.Ordinal) ||
                                      !RemediationTargetOperations.ForKind(target.Kind).Contains(operation, StringComparer.Ordinal)))
            return TargetEvaluation.Fail(TargetReadiness.OperationNotAllowed);
        if (!RemediationTargetKinds.IsKnown(target.Kind) || target.MachineId == Guid.Empty ||
            !Regex.IsMatch(target.ExpectedHostName, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") ||
            (isAppProcess ? target.WindowsServiceName.Length != 0 : !Regex.IsMatch(target.WindowsServiceName, @"^[A-Za-z0-9_.-]{1,256}$")))
            return TargetEvaluation.Fail(TargetReadiness.InvalidTarget);
        // Remediation is local-only: sc.exe against another host would authenticate to that SCM as
        // the backend's network identity with nothing binding the endpoint to the enrolled Agent.
        if (!_localMachine.IsLocal(target.ExpectedHostName))
            return TargetEvaluation.Fail(TargetReadiness.RemoteNotSupported);

        var machine = await _db.Machines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == target.MachineId, ct);
        if (machine is null || string.IsNullOrWhiteSpace(machine.AgentCredentialHash) ||
            !machine.HostName.Equals(target.ExpectedHostName, StringComparison.OrdinalIgnoreCase) ||
            !machine.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return TargetEvaluation.Fail(TargetReadiness.MachineMismatch);
        if (machine.LastSeenAt > DateTime.UtcNow.AddSeconds(5) ||
            machine.LastSeenAt < DateTime.UtcNow.AddSeconds(-Math.Clamp(_legacyOptions.MachineHeartbeatMaxAgeSeconds, 10, 300)))
            return TargetEvaluation.Fail(TargetReadiness.MachineOffline);

        // A target whose Windows service identity was never confirmed against the live SCM (legacy
        // appsettings import, or a row from before schema v11) must be re-confirmed by an operator.
        // An application-process target has no service identity; its process is identified below.
        if (!isAppProcess && string.IsNullOrWhiteSpace(entity.ServiceIdentityHash))
            return TargetEvaluation.Fail(TargetReadiness.StaleTarget);

        // A selected target and a machine-shaped incident are not proof of SDK origin. Require
        // recent evidence that this exact credential was confirmed by this enrolled Agent.
        // Re-pairing, Agent-key rotation, credential revocation and target rebinding all fail
        // closed here, including during the repeated pre-SCM fingerprint checks.
        var binding = await _db.SdkMachineBindings.AsNoTracking().SingleOrDefaultAsync(
            b => b.CredentialId == target.TelemetryCredentialId, ct);
        if (binding is null || binding.ProjectId != entity.ProjectId || binding.MachineId != machine.Id ||
            binding.AgentCredentialHash != machine.AgentCredentialHash ||
            binding.LastConfirmedAt < entity.UpdatedAt ||
            binding.LastConfirmedAt < DateTime.UtcNow.AddMinutes(-5))
            return TargetEvaluation.Fail(TargetReadiness.AwaitingAgentConfirmation);

        // Bundled into the same result WindowsServiceTool.TargetFingerprint needs, rather than
        // making that caller re-query this exact Machine row a second time.
        target.AgentCredentialHash = machine.AgentCredentialHash;

        if (isAppProcess)
        {
            // Which process: the one the Agent saw send this credential's confirmed telemetry (from
            // the OS TCP table), never a process id the application chose to report.
            if (binding.ProcessId is not int processId || string.IsNullOrWhiteSpace(binding.ProcessWorkingDirectory))
                return TargetEvaluation.Fail(TargetReadiness.ProcessUnknown);

            // Who can restart it: the UserAgent running in that process owner's signed-in session,
            // which reports the process itself (start time, executable) from the OS.
            var seenAfter = DateTime.UtcNow.AddSeconds(-Math.Clamp(_legacyOptions.MachineHeartbeatMaxAgeSeconds, 10, 300));
            var process = await _db.DiscoveredApplications.AsNoTracking()
                .Where(a => a.MachineId == machine.Id && a.Source == "UserAgent" && a.ProcessId == processId &&
                            a.IsRunning && a.SessionId != null && a.LastSeenAt >= seenAfter)
                .OrderByDescending(a => a.LastSeenAt)
                .FirstOrDefaultAsync(ct);
            if (process is null) return TargetEvaluation.Fail(TargetReadiness.UserAgentOffline);
            if (AppProcessEligibility.Refusal(process.Executable) is not null)
                return TargetEvaluation.Fail(TargetReadiness.Denylisted);

            target.ProcessId = processId;
            target.ProcessStartedAt = process.ProcessStartedAt;
            target.ProcessExecutable = process.Executable;
            target.ProcessWorkingDirectory = binding.ProcessWorkingDirectory!;
            target.SessionId = process.SessionId!.Value;
        }

        return new TargetEvaluation(target, TargetReadiness.Ready);
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
        AllowedOperations = RemediationTargetOperations.Deserialize(entity.AllowedOperationsJson),
        ServiceIdentityHash = entity.ServiceIdentityHash ?? "",
        Kind = entity.Kind
    };
}
