using Kairon.Backend.DTOs;
using Kairon.Backend.Services;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// AgentRegistrationService (docs/DESKTOP_SHELL.md): a machine registers once, then heartbeats
/// the processes its Agent's ProcessWatcher currently sees - "Basic Monitoring", no SDK involved.
/// </summary>
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
            new AgentHeartbeatDto
            {
                Processes =
                [
                    new ProcessSnapshotDto
                    {
                        ProcessId = 42,
                        StartedAt = DateTime.Parse("2026-08-27T17:59:00Z").ToUniversalTime(),
                        Name = "orders",
                        Runtime = ".NET",
                        CpuPercent = 12.5,
                        MemoryBytes = 1024
                    }
                ]
            }, default);

        Assert.True(accepted);
        var application = Assert.Single(_h.Db.DiscoveredApplications);
        Assert.True(application.IsRunning);
        Assert.Equal("orders", application.Name);
        Assert.Equal(machineId, application.MachineId);
    }

    [Fact]
    public async Task RegistrationWithTheKnownInsecureDefaultAgentKeyIsRejected()
    {
        var registration = Registration(Guid.NewGuid(), "kairon-agent-default-key-change-me");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.RegisterAsync(registration, default));
        Assert.Empty(_h.Db.Machines);
    }

    [Fact]
    public async Task MachineStuckOnTheInsecureDefaultCanRotateToARealKeyOnce()
    {
        var machineId = Guid.NewGuid();
        // Simulate a machine that registered before AgentCredentialStore existed - stored under
        // the insecure default's hash directly, bypassing the now-rejected literal-key check.
        _h.Db.Machines.Add(new Kairon.Backend.Models.Platform.Machine
        {
            Id = machineId,
            HostName = "old-host",
            RegisteredAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            AgentCredentialHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("kairon-agent-default-key-change-me")))
        });
        await _h.Db.SaveChangesAsync();

        var realKey = "a-genuinely-random-generated-key-1234567890";
        await Service.RegisterAsync(Registration(machineId, realKey), default);

        Assert.True(await Service.RecordHeartbeatAsync(machineId, realKey, new AgentHeartbeatDto(), default));
        // Having rotated once, a THIRD different key is rejected exactly as strictly as before.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service.RegisterAsync(Registration(machineId, "yet-another-different-key-1234567890"), default));
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

    [Fact]
    public async Task AuthenticatedLegacyRotationSeparatesKeysAndRejectsTheOldSharedCredential()
    {
        var machineId = Guid.NewGuid();
        const string oldSharedKey = "legacy-shared-key-that-is-long-enough";
        const string newAgentKey = "new-machine-agent-key-that-is-long-enough";
        const string newUserKey = "new-user-agent-key-that-is-long-enough";

        _h.Db.Machines.Add(new Kairon.Backend.Models.Platform.Machine
        {
            Id = machineId,
            HostName = "legacy-host",
            RegisteredAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            AgentCredentialHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(oldSharedKey)))
        });
        await _h.Db.SaveChangesAsync();

        var rotated = Registration(machineId, newAgentKey);
        rotated.UserAgentKey = newUserKey;
        rotated.PreviousAgentKey = oldSharedKey;
        await Service.RegisterAsync(rotated, default);

        Assert.True(await Service.RecordHeartbeatAsync(machineId, newAgentKey, new AgentHeartbeatDto(), default));
        Assert.True(await Service.RecordUserSessionHeartbeatAsync(
            machineId, newUserKey, new UserSessionHeartbeatDto(), default));
        Assert.False(await Service.RecordHeartbeatAsync(machineId, oldSharedKey, new AgentHeartbeatDto(), default));
        Assert.False(await Service.RecordUserSessionHeartbeatAsync(
            machineId, oldSharedKey, new UserSessionHeartbeatDto(), default));
        Assert.False(await Service.RecordHeartbeatAsync(machineId, newUserKey, new AgentHeartbeatDto(), default));
        Assert.False(await Service.RecordUserSessionHeartbeatAsync(
            machineId, newAgentKey, new UserSessionHeartbeatDto(), default));
    }

    [Fact]
    public async Task ProcessNoLongerReportedIsMarkedNotRunningNotDeleted()
    {
        var machineId = Guid.NewGuid();
        var registration = Registration(machineId, "correct-agent-key-that-is-long-enough");
        await Service.RegisterAsync(registration, default);
        await Service.RecordHeartbeatAsync(machineId, registration.AgentKey, new AgentHeartbeatDto
        {
            Processes = [new ProcessSnapshotDto { ProcessId = 42, StartedAt = DateTime.UtcNow, Name = "orders" }]
        }, default);

        // Next heartbeat reports nothing - the process is gone, but history is kept, not deleted.
        await Service.RecordHeartbeatAsync(machineId, registration.AgentKey, new AgentHeartbeatDto(), default);

        var application = Assert.Single(_h.Db.DiscoveredApplications);
        Assert.False(application.IsRunning);
    }

    [Fact]
    public async Task ReRegistrationWithTheSameKeyUpdatesMachineDetails()
    {
        var machineId = Guid.NewGuid();
        var key = "correct-agent-key-that-is-long-enough";
        await Service.RegisterAsync(Registration(machineId, key), default);

        await Service.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId,
            HostName = "host",
            OperatingSystem = "Windows",
            Architecture = "X64",
            AgentVersion = "2.0",
            AgentKey = key,
            UserAgentKey = UserKey(key)
        }, default);

        var machine = Assert.Single(_h.Db.Machines);
        Assert.Equal("2.0", machine.AgentVersion);
    }

    private static AgentRegistrationDto Registration(Guid id, string key) => new()
    {
        MachineId = id,
        HostName = "host",
        OperatingSystem = "Windows",
        Architecture = "X64",
        AgentVersion = "1.0",
        AgentKey = key,
        UserAgentKey = UserKey(key)
    };

    private static string UserKey(string machineKey) => $"user-{machineKey}";

    public void Dispose() => _h.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
