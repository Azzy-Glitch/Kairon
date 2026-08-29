using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Services;

/// <summary>Ported near-verbatim from Azzy's productization branch: register a machine identity
/// once, then accept periodic heartbeats reporting the processes the Agent's own process watcher
/// currently sees running. This is "Basic Monitoring" (docs/DESKTOP_SHELL.md) - no SDK, no
/// per-application pairing, just what the Agent can see on its own.</summary>
public interface IAgentRegistrationService
{
    Task RegisterAsync(AgentRegistrationDto registration, CancellationToken cancellationToken);
    Task<bool> RecordHeartbeatAsync(Guid machineId, string agentKey, AgentHeartbeatDto heartbeat,
        CancellationToken cancellationToken);
    Task<bool> RecordUserSessionHeartbeatAsync(Guid machineId, string agentKey, UserSessionHeartbeatDto heartbeat,
        CancellationToken cancellationToken);
}

public sealed class AgentRegistrationService : IAgentRegistrationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    public AgentRegistrationService(AppDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task RegisterAsync(AgentRegistrationDto registration, CancellationToken cancellationToken)
    {
        if (registration.MachineId == Guid.Empty) throw new ArgumentException("MachineId is required.");
        var now = _time.GetUtcNow().UtcDateTime;
        var hash = Hash(registration.AgentKey);
        var machine = await _db.Machines.SingleOrDefaultAsync(x => x.Id == registration.MachineId, cancellationToken);
        if (machine is null)
        {
            machine = new Machine { Id = registration.MachineId, RegisteredAt = now, AgentCredentialHash = hash };
            _db.Machines.Add(machine);
        }
        else if (!FixedEquals(machine.AgentCredentialHash, hash))
        {
            throw new UnauthorizedAccessException("Machine identity is already registered with a different Agent key.");
        }

        machine.HostName = registration.HostName;
        machine.OperatingSystem = registration.OperatingSystem;
        machine.Architecture = registration.Architecture;
        machine.AgentVersion = registration.AgentVersion;
        machine.LastSeenAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RecordHeartbeatAsync(Guid machineId, string agentKey, AgentHeartbeatDto heartbeat,
        CancellationToken cancellationToken)
    {
        var machine = await _db.Machines.SingleOrDefaultAsync(x => x.Id == machineId, cancellationToken);
        if (machine is null || !FixedEquals(machine.AgentCredentialHash, Hash(agentKey))) return false;

        var now = _time.GetUtcNow().UtcDateTime;
        machine.LastSeenAt = now;

        // Scoped to this source only: a UserAgent heartbeat for the same machine must not mark
        // MachineAgent-sourced rows not-running, and vice versa (docs/DESKTOP_SHELL.md).
        var applications = await _db.DiscoveredApplications
            .Where(x => x.MachineId == machineId && x.Source == "MachineAgent")
            .ToListAsync(cancellationToken);
        foreach (var existing in applications) existing.IsRunning = false;

        foreach (var snapshot in heartbeat.Processes
                     .Where(x => x.ProcessId > 0 && x.StartedAt != default && !string.IsNullOrWhiteSpace(x.Name))
                     .DistinctBy(x => (x.ProcessId, x.StartedAt))
                     .Take(500))
        {
            var application = applications.SingleOrDefault(x =>
                x.ProcessId == snapshot.ProcessId && x.ProcessStartedAt == snapshot.StartedAt);
            if (application is null)
            {
                application = new DiscoveredApplication
                {
                    Id = Guid.NewGuid(),
                    MachineId = machineId,
                    ProcessId = snapshot.ProcessId,
                    ProcessStartedAt = snapshot.StartedAt,
                    Source = "MachineAgent",
                    FirstSeenAt = now
                };
                _db.DiscoveredApplications.Add(application);
                applications.Add(application);
            }

            application.Name = snapshot.Name;
            application.Executable = snapshot.Executable;
            application.Runtime = snapshot.Runtime;
            application.CpuPercent = Math.Clamp(snapshot.CpuPercent, 0, 100);
            application.MemoryBytes = Math.Max(0, snapshot.MemoryBytes);
            application.IsRunning = true;
            application.LastSeenAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RecordUserSessionHeartbeatAsync(Guid machineId, string agentKey,
        UserSessionHeartbeatDto heartbeat, CancellationToken cancellationToken)
    {
        var machine = await _db.Machines.SingleOrDefaultAsync(x => x.Id == machineId, cancellationToken);
        if (machine is null || !FixedEquals(machine.AgentCredentialHash, Hash(agentKey))) return false;

        var now = _time.GetUtcNow().UtcDateTime;
        // Deliberately NOT machine.LastSeenAt - that field reflects only the Windows Service's own
        // heartbeat, so the machine's online/offline status never depends on whether any
        // interactive user session is running the UserAgent.
        machine.LastUserAgentSeenAt = now;

        var applications = await _db.DiscoveredApplications
            .Where(x => x.MachineId == machineId && x.Source == "UserAgent")
            .ToListAsync(cancellationToken);
        foreach (var existing in applications) existing.IsRunning = false;

        foreach (var snapshot in heartbeat.Processes
                     .Where(x => x.ProcessId > 0 && x.StartedAt != default && !string.IsNullOrWhiteSpace(x.Name))
                     .DistinctBy(x => (x.ProcessId, x.StartedAt))
                     .Take(500))
        {
            var application = applications.SingleOrDefault(x =>
                x.ProcessId == snapshot.ProcessId && x.ProcessStartedAt == snapshot.StartedAt);
            if (application is null)
            {
                application = new DiscoveredApplication
                {
                    Id = Guid.NewGuid(),
                    MachineId = machineId,
                    ProcessId = snapshot.ProcessId,
                    ProcessStartedAt = snapshot.StartedAt,
                    Source = "UserAgent",
                    FirstSeenAt = now
                };
                _db.DiscoveredApplications.Add(application);
                applications.Add(application);
            }

            application.Name = snapshot.Name;
            application.Executable = snapshot.Executable;
            application.Runtime = snapshot.Runtime;
            application.CpuPercent = Math.Clamp(snapshot.CpuPercent, 0, 100);
            application.MemoryBytes = Math.Max(0, snapshot.MemoryBytes);
            application.ParentProcessId = snapshot.ParentProcessId;
            application.SessionId = heartbeat.SessionId;
            application.UserName = heartbeat.UserName;
            application.IsRunning = true;
            application.LastSeenAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}
