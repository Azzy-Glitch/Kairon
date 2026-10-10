using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services.Remediation.Tools;

/// <summary>Which executables an application-process target may never point at: Windows itself
/// and KAIRON's own components. Applied by the resolver (target readiness) and again by the
/// UserAgent immediately before it acts.</summary>
public static class AppProcessEligibility
{
    public static string? Refusal(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            return "The application's executable path is unknown.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && IsUnder(executable, windows))
            return "Windows components are never restarted as applications.";
        var kaironInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kairon");
        if (IsUnder(executable, kaironInstall) || Path.GetFileName(executable).StartsWith("Kairon", StringComparison.OrdinalIgnoreCase))
            return "KAIRON's own components are never remediation targets.";
        return null;
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder.TrimEnd('\\', '/') + '/', StringComparison.OrdinalIgnoreCase);
}

/// <summary>The UserAgent's side of the restart queue: claim this session's approved restarts,
/// then report each outcome. Every query is scoped to the authenticated machine.</summary>
public interface IProcessRestartQueue
{
    Task<IReadOnlyList<ProcessRestartCommand>> ClaimAsync(Guid machineId, int sessionId, CancellationToken ct);
    Task<bool> CompleteAsync(Guid machineId, Guid commandId, bool succeeded, int? newProcessId, string? error, CancellationToken ct);
}

public sealed class ProcessRestartQueue(AppDbContext db, TimeProvider time) : IProcessRestartQueue
{
    public async Task<IReadOnlyList<ProcessRestartCommand>> ClaimAsync(Guid machineId, int sessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var pending = await db.ProcessRestartCommands
            .Where(c => c.MachineId == machineId && c.SessionId == sessionId && c.Status == ProcessRestartStatus.Pending)
            .ToListAsync(ct);
        var claimed = new List<ProcessRestartCommand>();
        foreach (var command in pending)
        {
            if (command.ExpiresAt <= now)
            {
                command.Status = ProcessRestartStatus.Expired;
            }
            else
            {
                command.Status = ProcessRestartStatus.Claimed;
                command.ClaimedAt = now;
                claimed.Add(command);
            }
            command.RowVersion = Guid.NewGuid();
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another claimant (or the tool expiring it) won the race; nothing is handed out twice.
            return [];
        }
        return claimed;
    }

    public async Task<bool> CompleteAsync(Guid machineId, Guid commandId, bool succeeded, int? newProcessId, string? error, CancellationToken ct)
    {
        var command = await db.ProcessRestartCommands.SingleOrDefaultAsync(c => c.Id == commandId && c.MachineId == machineId, ct);
        if (command is null || command.Status != ProcessRestartStatus.Claimed) return false;
        command.Status = succeeded ? ProcessRestartStatus.Succeeded : ProcessRestartStatus.Failed;
        command.CompletedAt = time.GetUtcNow().UtcDateTime;
        command.NewProcessId = succeeded && newProcessId is > 0 ? newProcessId : null;
        command.Error = succeeded ? null : Audit.Redaction.Scrub(Truncate(error, 1000)) ?? "The UserAgent reported a failure.";
        command.RowVersion = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>
/// Restarts the SDK-connected application's own process on an AppProcess target. KAIRON never
/// runs anything itself here: after approval and full target re-validation it queues one restart
/// instruction, which only the KAIRON UserAgent in the process owner's session can claim. That
/// UserAgent re-checks the process with the operating system and relaunches it with the command
/// line Windows reports for it. The AI selects nothing but this tool's name.
/// </summary>
public sealed class RestartApplicationTool(AppDbContext db, IRemediationTargetResolver targets, TimeProvider? time = null,
    Func<Guid, CancellationToken, Task>? afterQueued = null)
    : IRemediationTool, IScopedRemediationTool
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Test seam only (null in production): runs once the instruction is saved, before polling, so a
    // single-connection test can play the UserAgent's part deterministically.
    private readonly Func<Guid, CancellationToken, Task>? _afterQueued = afterQueued;

    /// <summary>How long the UserAgent has to pick an instruction up; it polls every few seconds.</summary>
    public static TimeSpan ClaimWindow { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>How long to wait for the outcome once claimed; well inside the executor's limit.</summary>
    public static TimeSpan CompletionWindow { get; set; } = TimeSpan.FromSeconds(12);
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public string Name => ServiceToolNames.RestartApplication;
    public string Description => "Restart the connected application's own process on its enrolled machine (the same program, " +
                                 "arguments and folder Windows reports for it), performed by the KAIRON UserAgent in the app owner's session. No arbitrary commands. " +
                                 "Clears in-process state - stuck threads or connections, leaked memory, runaway background work, bad cached state. " +
                                 "It does not fix a code defect that fails on every request, a misconfiguration, or an external dependency that is down.";
    public RiskLevel RiskLevel => RiskLevel.Medium;
    public IReadOnlyList<string> ExpectedMetricEffects => [];

    public bool ValidateParameters(IReadOnlyDictionary<string, string> parameters, out string? error)
    {
        var valid = parameters.Count == 1 && parameters.TryGetValue("targetFingerprint", out var fingerprint) &&
                    Regex.IsMatch(fingerprint, "^[A-F0-9]{64}$");
        error = valid ? null : "A server-generated target binding is required; additional parameters are forbidden.";
        return valid;
    }

    private Task<WindowsServiceTarget?> Target(Guid projectId, string environment, string service, CancellationToken ct) =>
        targets.ResolveExecutionTargetAsync(projectId, environment, service, Name, ct);

    public async Task<Guid?> TargetMachineIdAsync(SreIncident incident, CancellationToken ct = default) =>
        await TargetFingerprintAsync(incident, ct) is null ? null : IncidentMachineScope.GetMachineId(incident);

    /// <summary>
    /// Binds an approval to the application, not to one process id: the same target, machine,
    /// credential and executable. An app that restarted on its own since the recommendation is still
    /// the app the operator approved restarting; a different program now holding the connection,
    /// a rebound credential, a rotated Agent key or a changed target all invalidate it.
    /// </summary>
    public async Task<string?> TargetFingerprintAsync(SreIncident incident, CancellationToken ct = default)
    {
        var target = await Target(incident.ProjectId, incident.Environment, incident.Service, ct);
        if (target is null || target.Kind != RemediationTargetKinds.AppProcess ||
            IncidentMachineScope.GetMachineId(incident) != target.MachineId) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SreJson.Serialize(new
        {
            target.ProjectId, target.Environment, target.Service, target.MachineId, target.TelemetryCredentialId,
            target.ExpectedHostName, target.Kind, Operation = Name, target.AgentCredentialHash,
            Executable = target.ProcessExecutable.ToUpperInvariant()
        }))));
    }

    /// <summary>Recovered means: the restart was reported done, and a different process of the same
    /// application has since sent Agent-confirmed telemetry. Metrics are judged separately.</summary>
    public async Task<bool> IsDesiredStateAsync(SreIncident incident, string fingerprint, CancellationToken ct)
    {
        if (await TargetFingerprintAsync(incident, ct) != fingerprint) return false;
        var target = (await Target(incident.ProjectId, incident.Environment, incident.Service, ct))!;
        var restart = await db.ProcessRestartCommands.AsNoTracking()
            .Where(c => c.IncidentId == incident.Id && c.Status == ProcessRestartStatus.Succeeded)
            .OrderByDescending(c => c.CompletedAt)
            .FirstOrDefaultAsync(ct);
        if (restart?.CompletedAt is not DateTime completedAt) return false;
        var binding = await db.SdkMachineBindings.AsNoTracking()
            .SingleOrDefaultAsync(b => b.CredentialId == target.TelemetryCredentialId, ct);
        return binding is not null && binding.ProcessId is int current && current != restart.ProcessId &&
               binding.LastConfirmedAt > completedAt;
    }

    public async Task<RemediationToolResult> ExecuteAsync(RemediationToolContext context, CancellationToken cancellationToken = default)
    {
        var incident = await db.SreIncidents.FindAsync([context.IncidentId], cancellationToken);
        if (incident is null || incident.ProjectId != context.ProjectId || incident.Environment != context.Environment ||
            incident.Service != context.Service)
            return RemediationToolResult.Fail("Incident scope does not match execution context.");
        var fingerprint = await TargetFingerprintAsync(incident, cancellationToken);
        if (!ValidateParameters(context.Parameters, out var error) || fingerprint is null || context.Parameters["targetFingerprint"] != fingerprint)
            return RemediationToolResult.Fail(error ?? "Target changed, offline, unenrolled or not allowlisted; a new approval is required.");

        var target = (await Target(context.ProjectId, context.Environment, context.Service, cancellationToken))!;
        if (AppProcessEligibility.Refusal(target.ProcessExecutable) is { } refusal)
            return RemediationToolResult.Fail("Denylisted: " + refusal);

        var now = _time.GetUtcNow().UtcDateTime;
        var command = new ProcessRestartCommand
        {
            IncidentId = incident.Id, ActionKey = context.ActionKey, ProjectId = target.ProjectId,
            MachineId = target.MachineId, SessionId = target.SessionId, ProcessId = target.ProcessId,
            ProcessStartedAt = target.ProcessStartedAt, Executable = target.ProcessExecutable,
            WorkingDirectory = target.ProcessWorkingDirectory, CreatedAt = now, ExpiresAt = now + ClaimWindow
        };
        db.ProcessRestartCommands.Add(command);
        await db.SaveChangesAsync(cancellationToken);
        if (_afterQueued is not null) await _afterQueued(command.Id, cancellationToken);

        var claimDeadline = now + ClaimWindow + PollInterval;
        DateTime? completionDeadline = null;
        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken);
            var current = await db.ProcessRestartCommands.AsNoTracking().SingleAsync(c => c.Id == command.Id, cancellationToken);
            var clock = _time.GetUtcNow().UtcDateTime;
            switch (current.Status)
            {
                case ProcessRestartStatus.Succeeded:
                    return RemediationToolResult.Ok(
                        "The UserAgent restarted the application. Application recovery has not yet been verified.", new()
                        {
                            ["machineId"] = target.MachineId.ToString(), ["host"] = target.ExpectedHostName,
                            ["operation"] = Name, ["previousProcessId"] = current.ProcessId.ToString(),
                            ["newProcessId"] = current.NewProcessId?.ToString() ?? "", ["executable"] = current.Executable
                        });
                case ProcessRestartStatus.Failed:
                    return RemediationToolResult.Fail("ProcessRestartFailed: " + (current.Error ?? "the UserAgent reported a failure."));
                case ProcessRestartStatus.Expired:
                    return RemediationToolResult.Fail(UserAgentUnavailable);
                case ProcessRestartStatus.Pending when clock >= claimDeadline:
                    if (await ExpireIfPendingAsync(command.Id, cancellationToken)) return RemediationToolResult.Fail(UserAgentUnavailable);
                    break; // claimed at the last moment: wait for its outcome below
                case ProcessRestartStatus.Claimed:
                    completionDeadline ??= clock + CompletionWindow;
                    if (clock >= completionDeadline)
                        return RemediationToolResult.Fail("ProcessRestartUnconfirmed: the UserAgent took the restart but did not report " +
                                                          "the outcome in time. Check the application before approving another action.");
                    break;
            }
        }
    }

    private const string UserAgentUnavailable =
        "UserAgentOffline: no KAIRON UserAgent in the application owner's signed-in session picked up the restart. " +
        "Nothing was changed. Is that user signed in?";

    private async Task<bool> ExpireIfPendingAsync(Guid id, CancellationToken ct)
    {
        var command = await db.ProcessRestartCommands.SingleAsync(c => c.Id == id, ct);
        if (command.Status != ProcessRestartStatus.Pending) return false;
        command.Status = ProcessRestartStatus.Expired;
        command.RowVersion = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Detach only this row: the executor's own tracked incident and action must survive.
            db.Entry(command).State = EntityState.Detached;
            return false; // the UserAgent claimed it first
        }
    }
}
