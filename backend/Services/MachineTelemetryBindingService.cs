using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services.Remediation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Services;

public readonly record struct MachineTelemetryScope(bool InvalidAgentProof, Guid? MachineId);

public interface IMachineTelemetryBindingService
{
    Task<Guid?> CreateProofAsync(Guid projectId, string environment, string service,
        string bodySha256, string sdkKey, CancellationToken ct);
    Task<bool> ConfirmProofAsync(Guid proofId, Guid machineId, string agentKey, CancellationToken ct, int? processId = null);
    Task<MachineTelemetryScope> ResolveAsync(HttpRequest request, Guid projectId, string environment,
        string? service, CancellationToken ct);
}

/// <summary>
/// A copied project API key alone is never a machine credential. For each telemetry body the SDK
/// requests a short-lived, body-bound challenge; only the enrolled local Agent can confirm it,
/// using its own key. The backend consumes the challenge exactly once and assigns MachineId only
/// when the operator's enabled target names that same machine and SDK credential.
/// </summary>
public sealed class MachineTelemetryBindingService : IMachineTelemetryBindingService
{
    private readonly AppDbContext _db;
    private readonly IAgentRegistrationService _agents;
    private readonly IRemediationTargetResolver _targets;
    private readonly WindowsRemediationOptions _windows;

    public MachineTelemetryBindingService(AppDbContext db, IAgentRegistrationService agents,
        IRemediationTargetResolver targets, IOptions<WindowsRemediationOptions> windows)
    {
        _db = db;
        _agents = agents;
        _targets = targets;
        _windows = windows.Value;
    }

    public async Task<Guid?> CreateProofAsync(Guid projectId, string environment, string service,
        string bodySha256, string sdkKey, CancellationToken ct)
    {
        if (projectId == Guid.Empty || string.IsNullOrWhiteSpace(service) || service.Length > 200 ||
            string.IsNullOrWhiteSpace(environment) || environment.Length > 50 ||
            bodySha256.Length != 64 || !bodySha256.All(Uri.IsHexDigit) ||
            !await _db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct)) return null;
        var credential = await ResolveCredentialAsync(projectId, sdkKey, ct);
        if (credential is null) return null;
        var challenge = new SdkMachineProofChallenge
        {
            ProjectId = projectId, CredentialId = credential.Id,
            EnvironmentNormalized = environment.ToLowerInvariant(), Service = service,
            BodySha256 = bodySha256.ToUpperInvariant(), ExpiresAt = DateTime.UtcNow.AddSeconds(30)
        };
        _db.SdkMachineProofChallenges.Add(challenge);
        await _db.SaveChangesAsync(ct);
        return challenge.Id;
    }

    public async Task<bool> ConfirmProofAsync(Guid proofId, Guid machineId, string agentKey, CancellationToken ct, int? processId = null)
    {
        var now = DateTime.UtcNow;
        var machine = await _agents.AuthenticateMachineAsync(machineId, agentKey, ct);
        if (machine is null || !machine.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
            !HeartbeatFresh(machine, now)) return false;

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var challenge = await _db.SdkMachineProofChallenges.SingleOrDefaultAsync(x => x.Id == proofId, ct);
            if (challenge is null || challenge.ExpiresAt <= now || challenge.ConfirmedAt is not null ||
                challenge.ConsumedAt is not null || !await CredentialActiveAsync(challenge, ct)) return false;
            var prior = await _db.SdkMachineBindings.AsNoTracking().SingleOrDefaultAsync(
                x => x.CredentialId == challenge.CredentialId, ct);
            if (prior is not null && (prior.MachineId != machineId || prior.ProjectId != challenge.ProjectId))
                return false;
            challenge.MachineId = machineId;
            challenge.AgentCredentialHash = machine.AgentCredentialHash;
            challenge.ConfirmedAt = now;
            challenge.ProcessId = processId is > 0 ? processId : null;
            challenge.RowVersion = Guid.NewGuid();
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException) { return false; }
        catch (DbException) { return false; }
    }

    public async Task<MachineTelemetryScope> ResolveAsync(HttpRequest request, Guid projectId,
        string environment, string? service, CancellationToken ct)
    {
        var proofText = request.Headers["X-Kairon-Machine-Proof"].ToString();
        if (proofText.Length == 0) return new MachineTelemetryScope(false, null);
        if (!Guid.TryParse(proofText, out var proofId) || proofId == Guid.Empty ||
            request.Body is null || !request.Body.CanSeek || string.IsNullOrWhiteSpace(service))
            return new MachineTelemetryScope(true, null);

        // EnableBuffering is installed before MVC model binding on only these telemetry routes.
        // Compare the bytes actually received, not caller-supplied EventId, hostname or digest.
        byte[] body;
        try
        {
            request.Body.Position = 0;
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length is 0 or > 1_048_576) return new MachineTelemetryScope(true, null);
            body = buffer.ToArray();
            request.Body.Position = 0;
        }
        catch { return new MachineTelemetryScope(true, null); }
        var digest = Convert.ToHexString(SHA256.HashData(body));
        var key = request.Headers["X-Kairon-API-Key"].ToString();
        var credential = await ResolveCredentialAsync(projectId, key, ct);
        if (credential is null) return new MachineTelemetryScope(true, null);

        var now = DateTime.UtcNow;
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var challenge = await _db.SdkMachineProofChallenges.SingleOrDefaultAsync(x => x.Id == proofId, ct);
            if (challenge is null || challenge.CredentialId != credential.Id || challenge.ProjectId != projectId ||
                challenge.ExpiresAt <= now || challenge.ConfirmedAt is null || challenge.ConsumedAt is not null ||
                challenge.MachineId is null || challenge.AgentCredentialHash is null ||
                !string.Equals(challenge.EnvironmentNormalized, environment.ToLowerInvariant(), StringComparison.Ordinal) ||
                !string.Equals(challenge.Service, service, StringComparison.Ordinal) ||
                !string.Equals(challenge.BodySha256, digest, StringComparison.Ordinal))
                return new MachineTelemetryScope(true, null);

            var machine = await _db.Machines.AsNoTracking().SingleOrDefaultAsync(
                m => m.Id == challenge.MachineId, ct);
            if (machine is null || !HeartbeatFresh(machine, now) ||
                !string.Equals(machine.AgentCredentialHash, challenge.AgentCredentialHash, StringComparison.Ordinal) ||
                !await CredentialActiveAsync(challenge, ct))
                return new MachineTelemetryScope(true, null);

            var binding = await _db.SdkMachineBindings.SingleOrDefaultAsync(
                x => x.CredentialId == credential.Id, ct);
            if (binding is not null && (binding.MachineId != machine.Id || binding.ProjectId != projectId))
                return new MachineTelemetryScope(true, null);

            challenge.ConsumedAt = now;
            challenge.RowVersion = Guid.NewGuid();
            if (binding is null)
            {
                binding = new SdkMachineBinding
                {
                    CredentialId = credential.Id, ProjectId = projectId, MachineId = machine.Id,
                    AgentCredentialHash = machine.AgentCredentialHash, LastConfirmedAt = now
                };
                _db.SdkMachineBindings.Add(binding);
            }
            else
            {
                binding.AgentCredentialHash = machine.AgentCredentialHash;
                binding.LastConfirmedAt = now;
            }
            RecordProcess(binding, challenge.ProcessId, body);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var target = await _targets.ResolveTelemetryTargetAsync(projectId, machine.Id, environment,
                service!, ct);
            return target?.TelemetryCredentialId == credential.Id
                ? new MachineTelemetryScope(false, machine.Id)
                : new MachineTelemetryScope(false, null);
        }
        catch (DbUpdateConcurrencyException) { return new MachineTelemetryScope(true, null); }
        catch (DbUpdateException) { return new MachineTelemetryScope(true, null); }
        catch (DbException) { return new MachineTelemetryScope(true, null); }
    }

    /// <summary>
    /// The Agent-observed process id is the identity; the SDK's own report in this same body (its
    /// process id, working directory and executable) is accepted only when it names that very
    /// process. A different process, or a body without the report, never inherits another
    /// process's folder - the stale values are cleared instead.
    /// </summary>
    public static void RecordProcess(SdkMachineBinding binding, int? agentObservedProcessId, byte[] body)
    {
        var changed = binding.ProcessId != agentObservedProcessId;
        binding.ProcessId = agentObservedProcessId;
        if (agentObservedProcessId is not int pid)
        {
            binding.ProcessWorkingDirectory = binding.ProcessExecutable = null;
            return;
        }
        var (directory, executable) = SdkReportedProcess(body, pid);
        if (directory is not null)
        {
            binding.ProcessWorkingDirectory = directory;
            binding.ProcessExecutable = executable;
        }
        else if (changed)
        {
            binding.ProcessWorkingDirectory = binding.ProcessExecutable = null;
        }
    }

    private static (string? Directory, string? Executable) SdkReportedProcess(byte[] body, int pid)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!TryProperty(document.RootElement, "events", out var events) || events.ValueKind != JsonValueKind.Array)
                return (null, null);
            foreach (var item in events.EnumerateArray())
            {
                if (!TryProperty(item, "processId", out var id) || id.ValueKind != JsonValueKind.Number ||
                    !id.TryGetInt32(out var reported) || reported != pid ||
                    !TryProperty(item, "metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                    continue;
                var directory = PathValue(metadata, "process.cwd");
                if (directory is null) continue;
                return (directory, PathValue(metadata, "process.executable"));
            }
        }
        catch (JsonException) { }
        return (null, null);
    }

    private static string? PathValue(JsonElement metadata, string name)
    {
        if (!TryProperty(metadata, name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return !string.IsNullOrWhiteSpace(text) && text.Length <= 1024 && Path.IsPathFullyQualified(text) &&
               !text.Any(char.IsControl) ? text : null;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private bool HeartbeatFresh(Machine machine, DateTime now) =>
        machine.LastSeenAt <= now.AddSeconds(5) &&
        machine.LastSeenAt >= now.AddSeconds(-Math.Clamp(_windows.MachineHeartbeatMaxAgeSeconds, 10, 300));

    private Task<bool> CredentialActiveAsync(SdkMachineProofChallenge challenge, CancellationToken ct) =>
        _db.ProjectApiCredentials.AnyAsync(c => c.Id == challenge.CredentialId &&
            c.ProjectId == challenge.ProjectId && c.RevokedAt == null, ct);

    private async Task<ProjectApiCredential?> ResolveCredentialAsync(Guid projectId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 16) return null;
        var prefix = key[..Math.Min(12, key.Length)];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var candidates = await _db.ProjectApiCredentials.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.KeyPrefix == prefix && x.RevokedAt == null)
            .ToListAsync(ct);
        return candidates.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(x.KeyHash), Encoding.ASCII.GetBytes(hash)));
    }
}
