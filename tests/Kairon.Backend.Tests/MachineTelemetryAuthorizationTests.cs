using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

public class MachineTelemetryAuthorizationTests
{
    [Theory]
    [InlineData("exact", true)]
    [InlineData("other-key", false)]
    [InlineData("other-machine", false)]
    [InlineData("other-service", false)]
    [InlineData("other-environment", false)]
    [InlineData("revoked", false)]
    public async Task MachineEvidenceRequiresItsDedicatedCredential(string scenario, bool accepted)
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident();
        var credentials = new ProjectCredentialService(h.Db, Options.Create(new PlatformSecurityOptions()), TimeProvider.System);
        var credential = (await credentials.CreateAsync(incident.ProjectId, "target", default))!;
        var other = (await credentials.CreateAsync(incident.ProjectId, "other", default))!;
        var machine = new Machine { HostName = "enrolled", OperatingSystem = "Windows", AgentCredentialHash = "test" };
        h.Db.Machines.Add(machine); h.Db.SaveChanges();
        var target = new WindowsServiceTarget { ProjectId = incident.ProjectId, Environment = incident.Environment, Service = incident.Service, MachineId = machine.Id, TelemetryCredentialId = credential.Id };
        var controller = new TelemetryController(h.Db, null!, h.Queue, credentials, Options.Create(new PlatformSecurityOptions()), Options.Create(new WindowsRemediationOptions { Targets = [target] })) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Kairon-API-Key"] = scenario == "other-key" ? other.ApiKey : credential.ApiKey;
        if (scenario == "revoked") await credentials.RevokeAsync(incident.ProjectId, credential.Id, default);
        var result = await controller.CreateMetric(new MetricDto {
            ProjectId = incident.ProjectId, MachineId = scenario == "other-machine" ? Guid.NewGuid() : machine.Id,
            Environment = scenario == "other-environment" ? "Staging" : incident.Environment,
            Service = scenario == "other-service" ? "other" : incident.Service, RequestCount = 1
        }, default);
        if (accepted) { Assert.IsType<OkObjectResult>(result); Assert.Equal(machine.Id, h.Db.Metrics.Single().MachineId); }
        else { Assert.IsType<UnauthorizedObjectResult>(result); Assert.Empty(h.Db.Metrics); }
    }
}
