using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Sre;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services.Remediation.Tools;

public interface IScopedRemediationTool
{
    // Includes immutable target identity for approval binding. Null means not authorized.
    string? TargetFingerprint(SreIncident incident);
    Guid? TargetMachineId(SreIncident incident);
    Task<bool> IsRunningAsync(SreIncident incident, string fingerprint, CancellationToken ct);
}

public static class ServiceToolNames
{
    public const string RestartService = "RestartService";
    public const string StartService = "StartService";
    public const string StopService = "StopService";
    public const string RunHealthCheck = "RunHealthCheck";
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
        if (process.ExitCode != 0) throw new InvalidOperationException($"SCM {operation} failed with exit code {process.ExitCode}; check target permissions and state.");
        return output;
    }
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
    private readonly WindowsRemediationOptions _targets;
    private readonly IWindowsServiceControl _control;
    protected WindowsServiceTool(AppDbContext db, IOptions<WindowsRemediationOptions> targets, IWindowsServiceControl control)
        => (_db, _targets, _control) = (db, targets.Value, control);
    public abstract string Name { get; }
    public virtual string Description => $"{Name} on the incident's explicitly enrolled, allowlisted Windows service target. No arbitrary commands.";
    public virtual RiskLevel RiskLevel => RiskLevel.Medium;
    public IReadOnlyList<string> ExpectedMetricEffects => [];
    public bool ValidateParameters(IReadOnlyDictionary<string, string> parameters, out string? error)
    {
        var valid = parameters.Count == 1 && parameters.TryGetValue("targetFingerprint", out var fingerprint) && Regex.IsMatch(fingerprint, "^[A-F0-9]{64}$");
        error = valid ? null : "A server-generated target binding is required; additional parameters are forbidden.";
        return valid;
    }
    private WindowsServiceTarget? Target(Guid projectId, string environment, string service)
    {
        if (!ProductEnvironments.Contains(environment) || !_db.Projects.Any(p => p.Id == projectId && p.IsActive)) return null;
        var matches = _targets.Targets.Where(t => t.ProjectId == projectId && t.Environment.Equals(environment, StringComparison.OrdinalIgnoreCase) && t.Service == service).ToList();
        if (matches.Count != 1) return null;
        var target = matches[0];
        if (!_db.ProjectApiCredentials.Any(c => c.Id == target.TelemetryCredentialId && c.ProjectId == projectId && c.RevokedAt == null)) return null;
        if (!target.AllowedOperations.Contains(Name, StringComparer.Ordinal) || target.MachineId == Guid.Empty ||
            !Regex.IsMatch(target.ExpectedHostName, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") ||
            !Regex.IsMatch(target.WindowsServiceName, @"^[A-Za-z0-9_.-]{1,256}$")) return null;
        var machine = _db.Machines.AsNoTracking().SingleOrDefault(m => m.Id == target.MachineId);
        if (machine is null || string.IsNullOrWhiteSpace(machine.AgentCredentialHash) ||
            !machine.HostName.Equals(target.ExpectedHostName, StringComparison.OrdinalIgnoreCase) ||
            !machine.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
            machine.LastSeenAt > DateTime.UtcNow.AddSeconds(5) ||
            machine.LastSeenAt < DateTime.UtcNow.AddSeconds(-Math.Clamp(_targets.MachineHeartbeatMaxAgeSeconds, 10, 300))) return null;
        return target;
    }
    public Guid? TargetMachineId(SreIncident incident) => TargetFingerprint(incident) is null ? null : IncidentMachineScope.GetMachineId(incident);
    public async Task<bool> IsRunningAsync(SreIncident incident, string fingerprint, CancellationToken ct) {
        if (TargetFingerprint(incident) != fingerprint) return false;
        var target = Target(incident.ProjectId, incident.Environment, incident.Service)!;
        return await _control.QueryAsync(target.ExpectedHostName, target.WindowsServiceName, ct) == 4;
    }
    public string? TargetFingerprint(SreIncident incident)
    {
        var target = Target(incident.ProjectId, incident.Environment, incident.Service);
        if (target is null || IncidentMachineScope.GetMachineId(incident) != target.MachineId) return null;
        var machine = _db.Machines.AsNoTracking().Single(m => m.Id == target.MachineId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SreJson.Serialize(new {
            target.ProjectId, target.Environment, target.Service, target.MachineId, target.TelemetryCredentialId,
            target.ExpectedHostName, target.WindowsServiceName, Operation = Name, machine.AgentCredentialHash
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
        var fingerprint = TargetFingerprint(incident);
        if (!ValidateParameters(context.Parameters, out var error) || fingerprint is null || context.Parameters["targetFingerprint"] != fingerprint)
            return RemediationToolResult.Fail(error ?? "Target changed, offline, unenrolled or not allowlisted; a new approval is required.");
        var target = Target(context.ProjectId, context.Environment, context.Service)!;
        var lockKey = target.ExpectedHostName.ToUpperInvariant() + "\0" + target.WindowsServiceName.ToUpperInvariant();
        var targetLock = TargetLocks[(uint)StringComparer.Ordinal.GetHashCode(lockKey) % (uint)TargetLocks.Length];
        await targetLock.WaitAsync(ct);
        try {
        if (TargetFingerprint(incident) != fingerprint)
            return RemediationToolResult.Fail("Target authorization changed while waiting for execution.");
        var state = await _control.QueryAsync(target.ExpectedHostName, target.WindowsServiceName, ct);
        if (Name == ServiceToolNames.RunHealthCheck) {
            return state == 4 ? Result("SCM reports Running; application recovery still requires fresh telemetry.", target) : RemediationToolResult.Fail($"SCM state is {state}, not Running.");
        }
        // Reject transitional states and avoid restarting a service an operator deliberately stopped.
        if (Name == ServiceToolNames.RestartService && state != 4)
            return RemediationToolResult.Fail("Restart requires a service currently in Running state.");
        if (state is not (1 or 4)) return RemediationToolResult.Fail("Service is transitioning or paused; no change was issued.");
        if (Name is ServiceToolNames.StopService or ServiceToolNames.RestartService && state == 4) {
            await _control.ChangeAsync(target.ExpectedHostName, target.WindowsServiceName, false, ct);
            await WaitAsync(target, 1, ct);
        }
        if (Name is ServiceToolNames.StartService or ServiceToolNames.RestartService && (state == 1 || Name == ServiceToolNames.RestartService)) {
            ct.ThrowIfCancellationRequested();
            await _control.ChangeAsync(target.ExpectedHostName, target.WindowsServiceName, true, ct);
            await WaitAsync(target, 4, ct);
        }
        return Result("SCM operation completed. Application recovery has not yet been verified.", target);
        } finally { targetLock.Release(); }
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

public sealed class RestartServiceTool(AppDbContext db, IOptions<WindowsRemediationOptions> options, IWindowsServiceControl control) : WindowsServiceTool(db, options, control) { public override string Name => ServiceToolNames.RestartService; }
public sealed class StartServiceTool(AppDbContext db, IOptions<WindowsRemediationOptions> options, IWindowsServiceControl control) : WindowsServiceTool(db, options, control) { public override string Name => ServiceToolNames.StartService; }
public sealed class StopServiceTool(AppDbContext db, IOptions<WindowsRemediationOptions> options, IWindowsServiceControl control) : WindowsServiceTool(db, options, control) { public override string Name => ServiceToolNames.StopService; public override RiskLevel RiskLevel => RiskLevel.High; }
public sealed class ServiceHealthCheckTool(AppDbContext db, IOptions<WindowsRemediationOptions> options, IWindowsServiceControl control) : WindowsServiceTool(db, options, control) { public override string Name => ServiceToolNames.RunHealthCheck; public override RiskLevel RiskLevel => RiskLevel.Low; }
