using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.Models;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

public class TelemetryServiceFilterTests
{
    [Fact]
    public async Task LegacyPythonIncidentPersistsOptionalRequestCorrelationWithoutChangingAuthentication()
    {
        using var h = new TestHarness();
        h.EnsureProject();
        var credentials = new ProjectCredentialService(h.Db,
            Options.Create(new PlatformSecurityOptions { RequireTelemetryKey = true }),
            TimeProvider.System, new PlatformAuditService(h.Db, TimeProvider.System,
                NullLogger<PlatformAuditService>.Instance));
        var issued = await credentials.CreateAsync(h.ProjectId, "Python SDK", default);
        var controller = new TelemetryController(h.Db, null!, h.Queue, credentials,
            Options.Create(new PlatformSecurityOptions()), h.Targets)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Kairon-API-Key"] = issued!.ApiKey;
        var correlation = Guid.NewGuid().ToString();
        var result = await controller.CreateIncident(new TelemetryPayload
        {
            ProjectId = h.ProjectId, ApplicationName = "PythonOrders", Service = "PythonOrders",
            Environment = "Development", Endpoint = "/orders", Method = "GET",
            StatusCode = 200, RequestId = correlation,
            Error = "credential krn_abcdefghijklmnop rejected"
        }, default);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(correlation, Assert.Single(h.Db.Incidents).RequestId);
        Assert.DoesNotContain("krn_abcdefghijklmnop", Assert.Single(h.Db.Incidents).ErrorMessage);
    }

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
        var controller = new TelemetryController(h.Db, null!, h.Queue, null!, Options.Create(new PlatformSecurityOptions()), h.Targets);
        var result = Assert.IsType<OkObjectResult>(await controller.GetIncidents(h.ProjectId.ToString(), default, "PythonOrders"));
        Assert.Equal("PythonOrders", Assert.Single(Assert.IsType<List<Incident>>(result.Value)).Service);
        var other = Assert.IsType<OkObjectResult>(await controller.GetIncidents(Guid.NewGuid().ToString(), default, "PythonOrders"));
        Assert.Empty(Assert.IsType<List<Incident>>(other.Value));
    }
}
