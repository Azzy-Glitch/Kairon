using System.Text.RegularExpressions;
using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services.Remediation;

public enum RemediationTargetOperationOutcome { Success, NotFound, ValidationFailed, Conflict }

public sealed record RemediationTargetOperationResult(
    RemediationTargetOperationOutcome Outcome,
    RemediationTargetResponse? Target = null,
    string? Error = null,
    string? ErrorCode = null)
{
    public static RemediationTargetOperationResult Ok(RemediationTargetResponse target) =>
        new(RemediationTargetOperationOutcome.Success, target);

    public static RemediationTargetOperationResult NotFoundResult(string error, string code) =>
        new(RemediationTargetOperationOutcome.NotFound, Error: error, ErrorCode: code);

    public static RemediationTargetOperationResult Invalid(string error, string code) =>
        new(RemediationTargetOperationOutcome.ValidationFailed, Error: error, ErrorCode: code);

    public static RemediationTargetOperationResult ConflictResult(string error, string code) =>
        new(RemediationTargetOperationOutcome.Conflict, Error: error, ErrorCode: code);
}

public sealed record RemediationTargetFilter(Guid? ProjectId, Guid? MachineId, bool? Enabled, string? Environment);

/// <summary>
/// Operator-facing CRUD/validation for RemediationTarget (Phase 2 management API, built on the
/// Phase 1 persistence model - Models/Platform/RemediationTarget.cs). Deliberately separate from
/// IRemediationTargetResolver: the resolver is the read-only, high-frequency runtime path used by
/// Detection/Telemetry/Execution; this service is the low-frequency, security-sensitive write path
/// an authorized operator drives through RemediationTargetsController. This service never bypasses
/// the resolver's own re-resolution at execution time - it only ever changes what is persisted, and
/// every write here is re-validated by the resolver again the next time it is actually needed.
///
/// Validation is split in two tiers, matching the CREATE/UPDATE/ENABLE requirements:
///   - "Shape" (ValidateShape): environment enum, non-empty service, host name and Windows service
///     name syntax, and an allowed-operations set drawn only from the fixed known operations and
///     never empty. Enforced unconditionally - a disabled row must still hold well-formed data.
///   - "Relationships" (ValidateRelationshipsAsync): project exists and is active, machine exists
///     and is Windows-enrolled and matches ExpectedHostName, telemetry credential exists/active/
///     owned by the same project, and enabled-uniqueness. Enforced only when the resulting row
///     would be Enabled - a disabled target is explicitly allowed to reference a machine or
///     credential that has since gone stale (RemediationTarget.Enabled's own documented invariant),
///     and is fully re-validated again the moment it is enabled.
/// </summary>
public interface IRemediationTargetManagementService
{
    Task<IReadOnlyList<RemediationTargetResponse>> ListAsync(RemediationTargetFilter filter, CancellationToken ct = default);
    Task<RemediationTargetResponse?> GetAsync(Guid id, CancellationToken ct = default);
    Task<RemediationTargetOperationResult> CreateAsync(CreateRemediationTargetRequest request, string actor, CancellationToken ct = default);
    Task<RemediationTargetOperationResult> UpdateAsync(Guid id, UpdateRemediationTargetRequest request, string actor, CancellationToken ct = default);
    Task<RemediationTargetOperationResult> SetEnabledAsync(Guid id, bool enabled, string actor, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ValidateAsync(CreateRemediationTargetRequest request, CancellationToken ct = default);
}

public sealed class RemediationTargetManagementService : IRemediationTargetManagementService
{
    // Identical to RemediationTargetResolver.ResolveExecutionTargetAsync's patterns - the
    // management API must reject at write-time exactly what the runtime would otherwise silently
    // refuse to resolve at execution-time, so an operator gets a clear error instead of a target
    // that quietly never works.
    private static readonly Regex HostNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$", RegexOptions.Compiled);
    private static readonly Regex ServiceNamePattern = new(@"^[A-Za-z0-9_.-]{1,256}$", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IPlatformAuditService _audit;
    private readonly TimeProvider _time;

    public RemediationTargetManagementService(AppDbContext db, IPlatformAuditService audit, TimeProvider time)
    {
        _db = db;
        _audit = audit;
        _time = time;
    }

    public async Task<IReadOnlyList<RemediationTargetResponse>> ListAsync(RemediationTargetFilter filter, CancellationToken ct = default)
    {
        var query = _db.RemediationTargets.AsNoTracking().AsQueryable();
        if (filter.ProjectId.HasValue) query = query.Where(t => t.ProjectId == filter.ProjectId.Value);
        if (filter.MachineId.HasValue) query = query.Where(t => t.MachineId == filter.MachineId.Value);
        if (filter.Enabled.HasValue) query = query.Where(t => t.Enabled == filter.Enabled.Value);
        if (!string.IsNullOrWhiteSpace(filter.Environment)) query = query.Where(t => t.Environment == filter.Environment);

        var entities = await query.OrderByDescending(t => t.UpdatedAt).ToListAsync(ct);
        return await ToResponsesAsync(entities, ct);
    }

    public async Task<RemediationTargetResponse?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _db.RemediationTargets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
        return entity is null ? null : (await ToResponsesAsync([entity], ct)).Single();
    }

    public async Task<RemediationTargetOperationResult> CreateAsync(CreateRemediationTargetRequest request, string actor, CancellationToken ct = default)
    {
        var errors = ValidateShape(request);
        // Project existence is checked unconditionally, even for a disabled target: unlike Machine/
        // TelemetryCredential (loose Guid references), ProjectId carries a real FK constraint
        // (AppDbContext's cascade FK from Project) - inserting a row against a non-existent project
        // would otherwise surface as a raw DbUpdateException instead of a clean validation error.
        if (errors.Count == 0 && !await _db.Projects.AnyAsync(p => p.Id == request.ProjectId, ct))
            errors.Add("Project does not exist.");
        if (request.Enabled)
        {
            if (errors.Count == 0) await ValidateRelationshipsAsync(request.ProjectId, request.MachineId, request.TelemetryCredentialId, request.ExpectedHostName, errors, ct);
            if (errors.Count == 0 && await ConflictsWithEnabledTargetAsync(request.ProjectId, request.Environment, request.Service, excludeId: null, ct))
                return RemediationTargetOperationResult.ConflictResult(
                    "An enabled remediation target already exists for this project, environment and service.", "duplicate-target");
        }
        if (errors.Count > 0) return RemediationTargetOperationResult.Invalid(string.Join(" ", errors), "invalid-target");

        var now = _time.GetUtcNow().UtcDateTime;
        var entity = new RemediationTarget
        {
            ProjectId = request.ProjectId,
            Environment = request.Environment,
            Service = request.Service.Trim(),
            MachineId = request.MachineId,
            TelemetryCredentialId = request.TelemetryCredentialId,
            ExpectedHostName = request.ExpectedHostName.Trim(),
            WindowsServiceName = request.WindowsServiceName.Trim(),
            AllowedOperationsJson = RemediationTargetOperations.Serialize(request.AllowedOperations),
            Enabled = request.Enabled,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.RemediationTargets.Add(entity);
        _audit.Record("remediation-target.created", actor, "remediation-target", entity.Id.ToString(), entity.ProjectId, data: Snapshot(entity));
        await _db.SaveChangesAsync(ct);

        return RemediationTargetOperationResult.Ok((await ToResponsesAsync([entity], ct)).Single());
    }

    public async Task<RemediationTargetOperationResult> UpdateAsync(Guid id, UpdateRemediationTargetRequest request, string actor, CancellationToken ct = default)
    {
        var entity = await _db.RemediationTargets.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null) return RemediationTargetOperationResult.NotFoundResult("Remediation target not found.", "target-not-found");

        if (request.ExpectedUpdatedAt.HasValue && request.ExpectedUpdatedAt.Value != entity.UpdatedAt)
            return RemediationTargetOperationResult.ConflictResult(
                "The target was modified by someone else since it was last read. Reload and try again.", "stale-update");

        var errors = ValidateShape(request);
        // See CreateAsync's remarks: ProjectId carries a real FK constraint, so its existence is
        // checked unconditionally even when the resulting row would be disabled.
        if (errors.Count == 0 && !await _db.Projects.AnyAsync(p => p.Id == request.ProjectId, ct))
            errors.Add("Project does not exist.");
        if (request.Enabled)
        {
            if (errors.Count == 0) await ValidateRelationshipsAsync(request.ProjectId, request.MachineId, request.TelemetryCredentialId, request.ExpectedHostName, errors, ct);
            if (errors.Count == 0 && await ConflictsWithEnabledTargetAsync(request.ProjectId, request.Environment, request.Service, excludeId: entity.Id, ct))
                return RemediationTargetOperationResult.ConflictResult(
                    "An enabled remediation target already exists for this project, environment and service.", "duplicate-target");
        }
        if (errors.Count > 0) return RemediationTargetOperationResult.Invalid(string.Join(" ", errors), "invalid-target");

        var before = Snapshot(entity);
        entity.ProjectId = request.ProjectId;
        entity.Environment = request.Environment;
        entity.Service = request.Service.Trim();
        entity.MachineId = request.MachineId;
        entity.TelemetryCredentialId = request.TelemetryCredentialId;
        entity.ExpectedHostName = request.ExpectedHostName.Trim();
        entity.WindowsServiceName = request.WindowsServiceName.Trim();
        entity.AllowedOperationsJson = RemediationTargetOperations.Serialize(request.AllowedOperations);
        entity.Enabled = request.Enabled;
        entity.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        // Deliberately does not touch RemediationAction.ParametersJson or any previously-computed
        // targetFingerprint - an approved action's fingerprint is re-derived and compared against
        // the CURRENT target at execution time (WindowsServiceTool.TargetFingerprint), so changing
        // the target here naturally invalidates a stale approval ("target-changed") without this
        // service ever needing to know that fingerprints exist.
        _audit.Record("remediation-target.updated", actor, "remediation-target", entity.Id.ToString(), entity.ProjectId,
            data: new { Before = before, After = Snapshot(entity) });
        await _db.SaveChangesAsync(ct);

        return RemediationTargetOperationResult.Ok((await ToResponsesAsync([entity], ct)).Single());
    }

    public async Task<RemediationTargetOperationResult> SetEnabledAsync(Guid id, bool enabled, string actor, CancellationToken ct = default)
    {
        var entity = await _db.RemediationTargets.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null) return RemediationTargetOperationResult.NotFoundResult("Remediation target not found.", "target-not-found");

        if (enabled && !entity.Enabled)
        {
            // A disabled target may have gone stale while disabled (Enabled's own documented
            // invariant). Enabling it must not skip straight past validation just because it was
            // valid once, before it was disabled.
            var errors = ValidateShape(entity.Environment, entity.Service, entity.ExpectedHostName, entity.WindowsServiceName,
                RemediationTargetOperations.Deserialize(entity.AllowedOperationsJson));
            if (errors.Count == 0)
                await ValidateRelationshipsAsync(entity.ProjectId, entity.MachineId, entity.TelemetryCredentialId, entity.ExpectedHostName, errors, ct);
            if (errors.Count > 0)
                return RemediationTargetOperationResult.Invalid("This target cannot be enabled: " + string.Join(" ", errors), "invalid-target");

            if (await ConflictsWithEnabledTargetAsync(entity.ProjectId, entity.Environment, entity.Service, excludeId: entity.Id, ct))
                return RemediationTargetOperationResult.ConflictResult(
                    "An enabled remediation target already exists for this project, environment and service.", "duplicate-target");
        }

        if (entity.Enabled != enabled)
        {
            entity.Enabled = enabled;
            entity.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            _audit.Record(enabled ? "remediation-target.enabled" : "remediation-target.disabled", actor,
                "remediation-target", entity.Id.ToString(), entity.ProjectId, data: Snapshot(entity));
            await _db.SaveChangesAsync(ct);
        }

        return RemediationTargetOperationResult.Ok((await ToResponsesAsync([entity], ct)).Single());
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(CreateRemediationTargetRequest request, CancellationToken ct = default)
    {
        var errors = ValidateShape(request);
        if (errors.Count == 0)
            await ValidateRelationshipsAsync(request.ProjectId, request.MachineId, request.TelemetryCredentialId, request.ExpectedHostName, errors, ct);
        if (errors.Count == 0 && await ConflictsWithEnabledTargetAsync(request.ProjectId, request.Environment, request.Service, excludeId: null, ct))
            errors.Add("An enabled remediation target already exists for this project, environment and service.");
        return errors;
    }

    // --- Validation ---

    private static List<string> ValidateShape(CreateRemediationTargetRequest request) =>
        ValidateShape(request.Environment, request.Service, request.ExpectedHostName, request.WindowsServiceName, request.AllowedOperations);

    private static List<string> ValidateShape(string environment, string service, string expectedHostName,
        string windowsServiceName, IEnumerable<string> allowedOperations)
    {
        var errors = new List<string>();

        if (!ProductEnvironments.Contains(environment))
            errors.Add("Environment must be Development, Staging or Production.");
        if (string.IsNullOrWhiteSpace(service))
            errors.Add("Service is required.");
        if (string.IsNullOrWhiteSpace(expectedHostName) || !HostNamePattern.IsMatch(expectedHostName))
            errors.Add("Expected host name is not a valid host name.");
        if (string.IsNullOrWhiteSpace(windowsServiceName) || !ServiceNamePattern.IsMatch(windowsServiceName))
            errors.Add("Windows service name is not a valid Windows service name.");

        var requested = (allowedOperations ?? []).ToList();
        var unknown = requested.Where(o => !RemediationTargetOperations.IsKnown(o)).Distinct().ToList();
        if (unknown.Count > 0)
            errors.Add($"Unknown operation(s): {string.Join(", ", unknown)}.");
        else if (requested.Count == 0)
            errors.Add("At least one allowed operation is required.");

        return errors;
    }

    private async Task ValidateRelationshipsAsync(Guid projectId, Guid machineId, Guid telemetryCredentialId,
        string expectedHostName, List<string> errors, CancellationToken ct)
    {
        if (!await _db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct))
            errors.Add("Project does not exist or is not active.");

        var machine = await _db.Machines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == machineId, ct);
        if (machine is null)
        {
            errors.Add("Machine does not exist.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(machine.AgentCredentialHash))
                errors.Add("Machine has never completed Agent enrollment.");
            if (!machine.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                errors.Add("Machine's operating system is not Windows.");
            if (!machine.HostName.Equals(expectedHostName, StringComparison.OrdinalIgnoreCase))
                errors.Add("Expected host name does not match the machine's registered host name.");
        }

        if (!await _db.ProjectApiCredentials.AnyAsync(c => c.Id == telemetryCredentialId && c.ProjectId == projectId && c.RevokedAt == null, ct))
            errors.Add("Telemetry credential does not exist, is revoked, or does not belong to this project.");
    }

    private async Task<bool> ConflictsWithEnabledTargetAsync(Guid projectId, string environment, string service, Guid? excludeId, CancellationToken ct)
    {
        var normalizedEnvironment = environment.ToLowerInvariant();
        var query = _db.RemediationTargets.AsNoTracking()
            .Where(t => t.Enabled && t.ProjectId == projectId && t.Environment.ToLower() == normalizedEnvironment && t.Service == service);
        if (excludeId.HasValue) query = query.Where(t => t.Id != excludeId.Value);
        return await query.AnyAsync(ct);
    }

    // --- Mapping ---

    private async Task<IReadOnlyList<RemediationTargetResponse>> ToResponsesAsync(IReadOnlyList<RemediationTarget> entities, CancellationToken ct)
    {
        if (entities.Count == 0) return [];

        var projectIds = entities.Select(e => e.ProjectId).Distinct().ToList();
        var machineIds = entities.Select(e => e.MachineId).Distinct().ToList();
        var credentialIds = entities.Select(e => e.TelemetryCredentialId).Distinct().ToList();

        var projectNames = await _db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var machineHosts = await _db.Machines.AsNoTracking()
            .Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.HostName, ct);
        var credentialNames = await _db.ProjectApiCredentials.AsNoTracking()
            .Where(c => credentialIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return entities.Select(e => new RemediationTargetResponse
        {
            Id = e.Id,
            ProjectId = e.ProjectId,
            ProjectName = projectNames.GetValueOrDefault(e.ProjectId),
            Environment = e.Environment,
            Service = e.Service,
            MachineId = e.MachineId,
            MachineHostName = machineHosts.GetValueOrDefault(e.MachineId),
            TelemetryCredentialId = e.TelemetryCredentialId,
            TelemetryCredentialName = credentialNames.GetValueOrDefault(e.TelemetryCredentialId),
            ExpectedHostName = e.ExpectedHostName,
            WindowsServiceName = e.WindowsServiceName,
            AllowedOperations = RemediationTargetOperations.Deserialize(e.AllowedOperationsJson),
            Enabled = e.Enabled,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt
        }).ToList();
    }

    private static object Snapshot(RemediationTarget entity) => new
    {
        entity.ProjectId,
        entity.Environment,
        entity.Service,
        entity.MachineId,
        entity.TelemetryCredentialId,
        entity.ExpectedHostName,
        entity.WindowsServiceName,
        AllowedOperations = RemediationTargetOperations.Deserialize(entity.AllowedOperationsJson),
        entity.Enabled
    };
}
