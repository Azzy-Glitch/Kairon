using AIDIP.Backend.DTOs;
using AIDIP.Backend.Services;
using Xunit;

namespace AIDIP.Backend.Tests;

public sealed class AgentRegistrationTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-08-27T18:00:00Z"));
    private AgentRegistrationService Service => new(_h.Db, _time);

    [Fact]
    public async Task RegistrationAndAuthenticatedHeartbeatCreateBasicInventory()
    {
        var machineId = Guid.NewGuid();
        var registration = Registration(machineId, "correct-agent-key-that-is-long-enough");
        await Service.RegisterAsync(registration, default);

        var accepted = await Service.RecordHeartbeatAsync(machineId, registration.AgentKey,
            new AgentHeartbeatDto { Processes = [new ProcessSnapshotDto { ProcessId = 42,
                StartedAt = DateTime.Parse("2026-08-27T17:59:00Z").ToUniversalTime(), Name = "orders",
                Runtime = ".NET", CpuPercent = 12.5, MemoryBytes = 1024 }] }, default);

        Assert.True(accepted);
        var application = Assert.Single(_h.Db.DiscoveredApplications);
        Assert.True(application.IsRunning);
        Assert.Equal("orders", application.Name);
        Assert.Equal(machineId, application.MachineId);
    }

    [Fact]
    public async Task WrongOrReusedMachineCredentialIsRejected()
    {
        var machineId = Guid.NewGuid();
        await Service.RegisterAsync(Registration(machineId, "original-agent-key-that-is-long-enough"), default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service.RegisterAsync(Registration(machineId, "attacker-agent-key-that-is-long-enough"), default));
        Assert.False(await Service.RecordHeartbeatAsync(machineId, "wrong-key", new AgentHeartbeatDto(), default));
    }

    private static AgentRegistrationDto Registration(Guid id, string key) => new()
    {
        MachineId = id, HostName = "host", OperatingSystem = "Windows", Architecture = "X64",
        AgentVersion = "1.0", AgentKey = key
    };

    public void Dispose() => _h.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
