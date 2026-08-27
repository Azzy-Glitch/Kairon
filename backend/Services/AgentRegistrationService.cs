using System.Security.Cryptography;
using System.Text;
using AIDIP.Backend.DTOs;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Models;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Services;

public interface IAgentRegistrationService
{
    Task RegisterAsync(AgentRegistrationDto registration, CancellationToken cancellationToken);
    Task<bool> RecordHeartbeatAsync(Guid machineId, string agentKey, AgentHeartbeatDto heartbeat,
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
        var machine = await _db.Machines.Include(x => x.Applications)
            .SingleOrDefaultAsync(x => x.Id == machineId, cancellationToken);
        if (machine is null || !FixedEquals(machine.AgentCredentialHash, Hash(agentKey))) return false;

        var now = _time.GetUtcNow().UtcDateTime;
        machine.LastSeenAt = now;
        foreach (var existing in machine.Applications) existing.IsRunning = false;

        foreach (var snapshot in heartbeat.Processes
                     .Where(x => x.ProcessId > 0 && x.StartedAt != default && !string.IsNullOrWhiteSpace(x.Name))
                     .DistinctBy(x => (x.ProcessId, x.StartedAt)).Take(500))
        {
            var application = machine.Applications.SingleOrDefault(x =>
                x.ProcessId == snapshot.ProcessId && x.ProcessStartedAt == snapshot.StartedAt);
            if (application is null)
            {
                application = new DiscoveredApplication
                {
                    Id = Guid.NewGuid(), MachineId = machineId, ProcessId = snapshot.ProcessId,
                    ProcessStartedAt = snapshot.StartedAt, FirstSeenAt = now
                };
                _db.DiscoveredApplications.Add(application);
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

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}
