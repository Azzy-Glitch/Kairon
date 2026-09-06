using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

public class TelemetryServiceFilterTests
{
    [Fact]
    public async Task SharedProjectFilterRunsBeforeLimitAndDoesNotLeakOtherProjects()
    {
        using var h = new TestHarness();
        h.EnsureProject();
        var now = DateTime.UtcNow;
        h.Db.Incidents.Add(new Incident { Id = Guid.NewGuid(), ProjectId = h.ProjectId, Service = "PythonOrders", Timestamp = now.AddMinutes(-2) });
        for (var i = 0; i < 60; i++)
            h.Db.Incidents.Add(new Incident { Id = Guid.NewGuid(), ProjectId = h.ProjectId, Service = "DotnetPayments", Timestamp = now.AddSeconds(-i) });
        await h.Db.SaveChangesAsync();
        var controller = new TelemetryController(h.Db, null!, h.Queue, null!, Options.Create(new PlatformSecurityOptions()));
        var result = Assert.IsType<OkObjectResult>(await controller.GetIncidents(h.ProjectId.ToString(), default, "PythonOrders"));
        Assert.Equal("PythonOrders", Assert.Single(Assert.IsType<List<Incident>>(result.Value)).Service);
        var other = Assert.IsType<OkObjectResult>(await controller.GetIncidents(Guid.NewGuid().ToString(), default, "PythonOrders"));
        Assert.Empty(Assert.IsType<List<Incident>>(other.Value));
    }
}
