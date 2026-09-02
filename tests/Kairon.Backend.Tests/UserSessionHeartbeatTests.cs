using Kairon.Backend.DTOs;
using Kairon.Backend.Services;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// KAIRON.UserAgent's heartbeat (docs/DESKTOP_SHELL.md) - the per-interactive-session counterpart
/// to AgentRegistrationTests' machine-level heartbeat. Covers the behaviors the new two-component
/// architecture specifically requires: both components landing on one Machine with no separate
/// pairing, each source's stale-marking staying scoped to itself, and the machine's own
/// online/offline status staying independent of user-session telemetry.
/// </summary>
public sealed class UserSessionHeartbeatTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly FixedTimeProvider _time = new(DateTimeOffset.Parse("2026-08-29T18:00:00Z"));
    private AgentRegistrationService Service => new(_h.Db, _time);

    [Fact]
    public async Task UserSessionHeartbeatIsRejectedForAMachineThatNeverRegistered()
    {
        var accepted = await Service.RecordUserSessionHeartbeatAsync(Guid.NewGuid(), "any-key-long-enough",
            new UserSessionHeartbeatDto(), default);

        Assert.False(accepted);
    }

    [Fact]
    public async Task UserSessionHeartbeatWithWrongKeyIsRejected()
    {
        var machineId = Guid.NewGuid();
        await Service.RegisterAsync(Registration(machineId, "correct-agent-key-that-is-long-enough"), default);

        var accepted = await Service.RecordUserSessionHeartbeatAsync(machineId, "wrong-key-that-is-long-enough",
            new UserSessionHeartbeatDto(), default);

        Assert.False(accepted);
    }

    [Fact]
    public async Task MachineAndInteractiveCredentialsAreNotInterchangeable()
    {
        var machineId = Guid.NewGuid();
        var machineKey = "correct-agent-key-that-is-long-enough";
        var userKey = UserKey(machineKey);
        await Service.RegisterAsync(Registration(machineId, machineKey), default);

        Assert.False(await Service.RecordUserSessionHeartbeatAsync(
            machineId, machineKey, new UserSessionHeartbeatDto(), default));
        Assert.False(await Service.RecordHeartbeatAsync(
            machineId, userKey, new AgentHeartbeatDto(), default));
        Assert.True(await Service.RecordUserSessionHeartbeatAsync(
            machineId, userKey, new UserSessionHeartbeatDto(), default));
    }

    [Fact]
    public async Task UserSessionHeartbeatReusesTheSameMachineTheWindowsServiceAlreadyRegistered()
    {
        var machineId = Guid.NewGuid();
        var key = "correct-agent-key-that-is-long-enough";
        await Service.RegisterAsync(Registration(machineId, key), default);

        var accepted = await Service.RecordUserSessionHeartbeatAsync(machineId, UserKey(key), new UserSessionHeartbeatDto
        {
            SessionId = 2,
            UserName = "HOST\\alice",
            Processes =
            [
                new UserProcessSnapshotDto
                {
                    ProcessId = 100,
                    StartedAt = DateTime.Parse("2026-08-29T17:59:00Z").ToUniversalTime(),
                    Name = "notepad",
                    CpuPercent = 3.2,
                    MemoryBytes = 2048,
                    ParentProcessId = 4
                }
            ]
        }, default);

        Assert.True(accepted);
        Assert.Single(_h.Db.Machines); // no duplicate machine created for the second source
        var application = Assert.Single(_h.Db.DiscoveredApplications);
        Assert.Equal("UserAgent", application.Source);
        Assert.Equal(2, application.SessionId);
        Assert.Equal("HOST\\alice", application.UserName);
        Assert.Equal(4, application.ParentProcessId);
    }

    [Fact]
    public async Task MachineAgentAndUserAgentHeartbeatsDoNotClobberEachOthersRows()
    {
        var machineId = Guid.NewGuid();
        var key = "correct-agent-key-that-is-long-enough";
        await Service.RegisterAsync(Registration(machineId, key), default);

        await Service.RecordHeartbeatAsync(machineId, key, new AgentHeartbeatDto
        {
            Processes = [new ProcessSnapshotDto { ProcessId = 1, StartedAt = DateTime.UtcNow, Name = "machine-watched" }]
        }, default);

        await Service.RecordUserSessionHeartbeatAsync(machineId, UserKey(key), new UserSessionHeartbeatDto
        {
            SessionId = 1,
            UserName = "HOST\\bob",
            Processes = [new UserProcessSnapshotDto { ProcessId = 2, StartedAt = DateTime.UtcNow, Name = "chrome", CpuPercent = 1 }]
        }, default);

        Assert.Equal(2, _h.Db.DiscoveredApplications.Count());
        Assert.True(_h.Db.DiscoveredApplications.Single(a => a.Source == "MachineAgent").IsRunning);
        Assert.True(_h.Db.DiscoveredApplications.Single(a => a.Source == "UserAgent").IsRunning);

        // A second UserAgent heartbeat reporting nothing must only mark the UserAgent row stale,
        // never the MachineAgent one.
        await Service.RecordUserSessionHeartbeatAsync(machineId, UserKey(key), new UserSessionHeartbeatDto
        {
            SessionId = 1,
            UserName = "HOST\\bob"
        }, default);

        Assert.True(_h.Db.DiscoveredApplications.Single(a => a.Source == "MachineAgent").IsRunning);
        Assert.False(_h.Db.DiscoveredApplications.Single(a => a.Source == "UserAgent").IsRunning);
    }

    [Fact]
    public async Task MachineStaysOnlineViaItsOwnHeartbeatRegardlessOfUserAgentPresence()
    {
        var machineId = Guid.NewGuid();
        var key = "correct-agent-key-that-is-long-enough";
        await Service.RegisterAsync(Registration(machineId, key), default);
        await Service.RecordHeartbeatAsync(machineId, key, new AgentHeartbeatDto(), default);

        var machine = Assert.Single(_h.Db.Machines);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, machine.LastSeenAt);
        Assert.Null(machine.LastUserAgentSeenAt); // UserAgent never connected - machine still Online via LastSeenAt
    }

    [Fact]
    public async Task UserSessionHeartbeatNeverTouchesTheMachineLevelLastSeenAt()
    {
        var machineId = Guid.NewGuid();
        var key = "correct-agent-key-that-is-long-enough";
        await Service.RegisterAsync(Registration(machineId, key), default);
        var registeredAt = _h.Db.Machines.Single().LastSeenAt;

        await Service.RecordUserSessionHeartbeatAsync(machineId, UserKey(key), new UserSessionHeartbeatDto
        {
            SessionId = 1,
            UserName = "HOST\\carol"
        }, default);

        var machine = _h.Db.Machines.Single();
        Assert.Equal(registeredAt, machine.LastSeenAt); // untouched by the user-session heartbeat
        Assert.NotNull(machine.LastUserAgentSeenAt);
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
