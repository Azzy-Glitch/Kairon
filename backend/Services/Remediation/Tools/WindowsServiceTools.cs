using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Remediation;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services.Remediation.Tools;

public interface IScopedRemediationTool
{
    // Includes immutable target identity for approval binding. Null means not authorized. Fully
    // async: resolving/validating the target is a real EF/database read, and every caller either
    // already runs asynchronously or is a thin, easily-async-ified wrapper around one - there is no
    // genuine need for a synchronous-blocking bridge here (see WindowsServiceTool's remarks on the
    // one bridge that DOES remain, and why).
    Task<string?> TargetFingerprintAsync(SreIncident incident, CancellationToken ct = default);
    Task<Guid?> TargetMachineIdAsync(SreIncident incident, CancellationToken ct = default);
    Task<bool> IsDesiredStateAsync(SreIncident incident, string fingerprint, CancellationToken ct);
}

public static class ServiceToolNames
{
    public const string RestartService = "RestartService";
    public const string StartService = "StartService";
    public const string StopService = "StopService";
    public const string RunHealthCheck = "RunHealthCheck";

    /// <summary>Restart the SDK-connected application's own process (AppProcess targets only).</summary>
    public const string RestartApplication = "RestartApplication";
}

public interface IWindowsServiceControl
{
    Task<int> QueryAsync(string host, string service, CancellationToken ct);
    Task ChangeAsync(string host, string service, bool start, CancellationToken ct);
}

// Fixed Windows SCM operations, using the backend's Windows identity and the target's SCM ACL.
// No shell, scripts, credentials, executable paths or arbitrary arguments from the AI.
public sealed class WindowsServiceControl : IWindowsServiceControl
{
    public async Task<int> QueryAsync(string host, string service, CancellationToken ct)
    {
        var output = await RunAsync(host, service, "query", ct);
        var match = Regex.Match(output, @":\s*([1-7])\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b");
        if (!match.Success) throw new InvalidOperationException("SCM did not return a recognized service state.");
        return int.Parse(match.Groups[1].Value);
    }
    public async Task ChangeAsync(string host, string service, bool start, CancellationToken ct) =>
        _ = await RunAsync(host, service, start ? "start" : "stop", ct);

    private static async Task<string> RunAsync(string host, string service, string operation, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows SCM execution requires a Windows backend.");
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(@"\\" + host);
        info.ArgumentList.Add(operation);
        info.ArgumentList.Add(service);
        using var process = new Process { StartInfo = info };
        process.Start();
        using var terminate = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        var stdout = ReadBoundedAsync(process.StandardOutput, ct);
        var stderr = ReadBoundedAsync(process.StandardError, ct);
        await process.WaitForExitAsync(ct);
        var output = await stdout;
        await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(DescribeScmFailure(operation, process.ExitCode));
        return output;
    }

    /// <summary>Operator-readable reason for an sc.exe failure. The Win32 code is kept for the
    /// advanced view; the prefix is the same classification the pre-flight reports.</summary>
    public static string DescribeScmFailure(string operation, int exitCode) => exitCode switch
    {
        5 => $"PermissionMissing: Windows denied the backend's identity permission to {operation} this service (SCM error 5, ERROR_ACCESS_DENIED).",
        1060 => $"ServiceMissing: the service does not exist (SCM error 1060, ERROR_SERVICE_DOES_NOT_EXIST).",
        1051 => $"DependentServicesRunning: other running services depend on this one, so it was not stopped (SCM error 1051).",
        1053 => $"ServiceTimeout: the service did not respond to the {operation} request in time (SCM error 1053).",
        1056 => $"AlreadyRunning: the service is already running (SCM error 1056).",
        1058 => $"ServiceDisabled: the service is disabled and cannot be started (SCM error 1058).",
        1062 => $"NotStarted: the service is not running (SCM error 1062).",
        1061 => $"ServiceBusy: the service cannot accept control messages right now (SCM error 1061).",
        _ => $"ScmFailed: SCM {operation} failed with exit code {exitCode}."
    };
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0) {
            if (result.Length + read > 16384) throw new InvalidOperationException("SCM output exceeded the allowed bound.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}

public abstract class WindowsServiceTool : IRemediationTool, IScopedRemediationTool
{
    // Fixed striped locks bound memory and serialize the same physical service across incidents.
    // Hash collisions only reduce concurrency; they cannot permit overlapping operations.
    private static readonly SemaphoreSlim[] TargetLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly AppDbContext _db;
    private readonly IRemediationTargetResolver _targets;
    private readonly IWindowsServiceControl _control;
    private readonly IWindowsServiceInspector _inspector;
    protected WindowsServiceTool(AppDbContext db, IRemediationTargetResolver targets, IWindowsServiceControl control,
        IWindowsServiceInspector? inspector = null)
        => (_db, _targets, _control, _inspector) = (db, targets, control, inspector ?? WindowsServiceInspector.Instance);
    public abstract string Name { get; }
    public virtual string Description => $"{Name} on the incident's explicitly enrolled, allowlisted Windows service target. No arbitrary commands." + Name switch
    {
        ServiceToolNames.RestartService => " Clears the service process's in-process state (stuck threads or connections, leaked memory); it does not fix a code defect, a misconfiguration or a failing external dependency.",
        ServiceToolNames.StartService => " Only useful when the service has stopped.",
        ServiceToolNames.StopService => " Causes an outage until the service is started again.",
        ServiceToolNames.RunHealthCheck => " Read-only: reports the service state and changes nothing.",
        _ => string.Empty
    };
    public virtual RiskLevel RiskLevel => RiskLevel.Medium;
    public IReadOnlyList<string> ExpectedMetricEffects => [];
    public bool ValidateParameters(IReadOnlyDictionary<string, string> parameters, out string? error)
    {
        var valid = parameters.Count == 1 && parameters.TryGetValue("targetFingerprint", out var fingerprint) && Regex.IsMatch(fingerprint, "^[A-F0-9]{64}$");
        error = valid ? null : "A server-generated target binding is required; additional parameters are forbidden.";
        return valid;
    }
    // Centralized in RemediationTargetResolver.ResolveExecutionTargetAsync (the former inline body
    // of this method) - the database-backed replacement for WindowsRemediation:Targets.
    private Task<WindowsServiceTarget?> Target(Guid projectId, string environment, string service) =>
        _targets.ResolveExecutionTargetAsync(projectId, environment, service, Name);
    public async Task<Guid?> TargetMachineIdAsync(SreIncident incident, CancellationToken ct = default) =>
        await TargetFingerprintAsync(incident, ct) is null ? null : IncidentMachineScope.GetMachineId(incident);
    public async Task<bool> IsDesiredStateAsync(SreIncident incident, string fingerprint, CancellationToken ct) {
        if (await TargetFingerprintAsync(incident, ct) != fingerprint) return false;
        var target = (await Target(incident.ProjectId, incident.Environment, incident.Service))!;
        return await _control.QueryAsync(target.ExpectedHostName, target.WindowsServiceName, ct) == (Name == ServiceToolNames.StopService ? 1 : 4);
    }
    public async Task<string?> TargetFingerprintAsync(SreIncident incident, CancellationToken ct = default)
    {
        // A genuine, fresh re-resolve every call - never cached, never assumed unchanged from a
        // prior call in the same request. This is what makes re-checking it immediately before an
        // irreversible SCM operation (ExecuteAsync below) actually mean something: a target
        // mutated, disabled, rebound to a different credential, or a machine that dropped its
        // heartbeat all change - or null out - the fingerprint this recomputes.
        var target = await Target(incident.ProjectId, incident.Environment, incident.Service);
        if (target is null || IncidentMachineScope.GetMachineId(incident) != target.MachineId) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SreJson.Serialize(new {
            target.ProjectId, target.Environment, target.Service, target.MachineId, target.TelemetryCredentialId,
            target.ExpectedHostName, target.WindowsServiceName, Operation = Name, target.AgentCredentialHash,
            target.ServiceIdentityHash
        }))));
    }
    public async Task<RemediationToolResult> ExecuteAsync(RemediationToolContext context, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var incident = await _db.SreIncidents.FindAsync([context.IncidentId], ct);
        if (incident is null || incident.ProjectId != context.ProjectId || incident.Environment != context.Environment || incident.Service != context.Service)
            return RemediationToolResult.Fail("Incident scope does not match execution context.");
        var fingerprint = await TargetFingerprintAsync(incident, ct);
        if (!ValidateParameters(context.Parameters, out var error) || fingerprint is null || context.Parameters["targetFingerprint"] != fingerprint)
            return RemediationToolResult.Fail(error ?? "Target changed, offline, unenrolled or not allowlisted; a new approval is required.");
        var target = (await Target(context.ProjectId, context.Environment, context.Service))!;
        var lockKey = target.ExpectedHostName.ToUpperInvariant() + "\0" + target.WindowsServiceName.ToUpperInvariant();
        var targetLock = TargetLocks[(uint)StringComparer.Ordinal.GetHashCode(lockKey) % (uint)TargetLocks.Length];
        await targetLock.WaitAsync(ct);
        try {
        if (await TargetFingerprintAsync(incident, ct) != fingerprint)
            return RemediationToolResult.Fail("Target authorization changed while waiting for execution.");
        if (LiveServiceRefusal(target) is { } refusal) return RemediationToolResult.Fail(refusal);
        var state = await _control.QueryAsync(target.ExpectedHostName, target.WindowsServiceName, ct);
        if (Name == ServiceToolNames.RunHealthCheck) {
            return state == 4 ? Result("SCM reports Running; application recovery still requires fresh telemetry.", target) : RemediationToolResult.Fail($"SCM state is {state}, not Running.");
        }
        // Reject transitional states and avoid restarting a service an operator deliberately stopped.
        if (Name == ServiceToolNames.RestartService && state != 4)
            return RemediationToolResult.Fail("Restart requires a service currently in Running state.");
        if (state is not (1 or 4)) return RemediationToolResult.Fail("Service is transitioning or paused; no change was issued.");
        // Re-validated once more, immediately before each irreversible state-changing SCM call -
        // narrows the TOCTOU window to "however long QueryAsync's own round-trip just took", not
        // the whole approval-to-execution lifetime. A target mutated (or a machine that dropped
        // its heartbeat) in that narrow window is caught here rather than acted upon.
        if (Name is ServiceToolNames.StopService or ServiceToolNames.RestartService && state == 4) {
            if (await TargetFingerprintAsync(incident, ct) != fingerprint)
                return RemediationToolResult.Fail("Target authorization changed while waiting for execution.");
            await _control.ChangeAsync(target.ExpectedHostName, target.WindowsServiceName, false, ct);
            await WaitAsync(target, 1, ct);
        }
        if (Name is ServiceToolNames.StartService or ServiceToolNames.RestartService && (state == 1 || Name == ServiceToolNames.RestartService)) {
            ct.ThrowIfCancellationRequested();
            if (await TargetFingerprintAsync(incident, ct) != fingerprint)
                return RemediationToolResult.Fail("Target authorization changed while waiting for execution.");
            await _control.ChangeAsync(target.ExpectedHostName, target.WindowsServiceName, true, ct);
            await WaitAsync(target, 4, ct);
        }
        return Result("SCM operation completed. Application recovery has not yet been verified.", target);
        } finally { targetLock.Release(); }
    }
    /// <summary>Re-reads the live local service immediately before any SCM call. Refuses (never
    /// relaxes) when it is no longer the service the operator authorized, is not an eligible
    /// application service, or the backend identity lacks the exact right this operation needs -
    /// so a missing permission is reported as such rather than as an opaque sc.exe failure.</summary>
    private string? LiveServiceRefusal(WindowsServiceTarget target)
    {
        if (!_inspector.IsSupported) return "UnsupportedPlatform: Windows service remediation requires a Windows backend.";
        var probe = _inspector.Probe(target.WindowsServiceName);
        if (!probe.Exists) return "ServiceMissing: the authorized Windows service no longer exists.";
        if (probe.Eligibility != ServiceEligibility.Eligible)
            return "Denylisted: " + ServiceEligibilityPolicy.Describe(probe.Eligibility);
        if (string.IsNullOrEmpty(target.ServiceIdentityHash) || probe.IdentityHash != target.ServiceIdentityHash)
            return "ServiceIdentityChanged: the service's executable or account changed since the target was confirmed; re-confirm the target.";
        if (!probe.HasRightsFor(Name))
            return $"PermissionMissing: the KAIRON backend identity lacks the Windows right(s) {string.Join(", ", WindowsServiceProbe.RequiredRights([Name]))} on this service.";
        return null;
    }

    private async Task WaitAsync(WindowsServiceTarget target, int expected, CancellationToken ct) {
        while (await _control.QueryAsync(target.ExpectedHostName, target.WindowsServiceName, ct) != expected)
            await Task.Delay(250, ct);
    }
    private RemediationToolResult Result(string message, WindowsServiceTarget target) => RemediationToolResult.Ok(message, new() {
        ["machineId"] = target.MachineId.ToString(), ["host"] = target.ExpectedHostName,
        ["windowsService"] = target.WindowsServiceName, ["operation"] = Name
    });
}

public sealed class RestartServiceTool(AppDbContext db, IRemediationTargetResolver targets, IWindowsServiceControl control, IWindowsServiceInspector? inspector = null) : WindowsServiceTool(db, targets, control, inspector) { public override string Name => ServiceToolNames.RestartService; }
public sealed class StartServiceTool(AppDbContext db, IRemediationTargetResolver targets, IWindowsServiceControl control, IWindowsServiceInspector? inspector = null) : WindowsServiceTool(db, targets, control, inspector) { public override string Name => ServiceToolNames.StartService; }
public sealed class StopServiceTool(AppDbContext db, IRemediationTargetResolver targets, IWindowsServiceControl control, IWindowsServiceInspector? inspector = null) : WindowsServiceTool(db, targets, control, inspector) { public override string Name => ServiceToolNames.StopService; public override RiskLevel RiskLevel => RiskLevel.High; }
public sealed class ServiceHealthCheckTool(AppDbContext db, IRemediationTargetResolver targets, IWindowsServiceControl control, IWindowsServiceInspector? inspector = null) : WindowsServiceTool(db, targets, control, inspector) { public override string Name => ServiceToolNames.RunHealthCheck; public override RiskLevel RiskLevel => RiskLevel.Low; }
