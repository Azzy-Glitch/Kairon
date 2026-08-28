using AIDIP.Backend.Configuration;
using AIDIP.Backend.Controllers;
using AIDIP.Backend.Models;
using AIDIP.Backend.Services;
using AIDIP.Backend.Services.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIDIP.Backend.Tests;

public sealed class PlatformApplicationTests : IDisposable
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task DiscoveredAgentApplicationCanBeRegisteredForDeepMonitoringWithoutDatabaseConfiguration()
    {
        var discovered = new DiscoveredApplication { Id = Guid.NewGuid(), MachineId = Guid.NewGuid(),
            ProcessId = 42, ProcessStartedAt = DateTime.UtcNow, Name = "Orders API", Runtime = ".NET",
            FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
        _h.Db.Machines.Add(new Machine { Id = discovered.MachineId, HostName = "test", AgentCredentialHash = "hash",
            RegisteredAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        _h.Db.DiscoveredApplications.Add(discovered);
        await _h.Db.SaveChangesAsync();
        var controller = new PlatformController(_h.Db,
            new ProjectCredentialService(_h.Db, Options.Create(new PlatformSecurityOptions()), TimeProvider.System),
            TimeProvider.System, new PlatformAuditService(_h.Db, TimeProvider.System,
                NullLogger<PlatformAuditService>.Instance))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.RegisterDiscoveredApplication(discovered.Id, default);
        var repeated = await controller.RegisterDiscoveredApplication(discovered.Id, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.IsType<OkObjectResult>(repeated);
        Assert.Single(_h.Db.Projects);
        Assert.Single(_h.Db.MonitoredApplications);
        Assert.Equal("orders-api", _h.Db.MonitoredApplications.Single().Service);
        Assert.Contains(_h.Db.PlatformAuditEvents, x => x.Action == "application.registered");
    }

    public void Dispose() => _h.Dispose();
}
