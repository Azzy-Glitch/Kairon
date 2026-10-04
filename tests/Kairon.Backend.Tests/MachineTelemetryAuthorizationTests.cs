using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Kairon.Backend.Tests;

public class MachineTelemetryAuthorizationTests
{
    [Fact]
    public async Task ExecutionFailsClosedWhenAgentBindingBecomesStaleRotatedOrTargetChanges()
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident();
        var machine = h.SeedMachine();
        var credential = h.SeedCredential(incident.ProjectId);
        var target = h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName,
            environment: incident.Environment, service: incident.Service);
        var binding = h.Db.SdkMachineBindings.Single();

        async Task<bool> Eligible() => await h.Targets.ResolveExecutionTargetAsync(
            incident.ProjectId, incident.Environment, incident.Service,
            ServiceToolNames.RestartService) is not null;
        Assert.True(await Eligible());

        binding.LastConfirmedAt = DateTime.UtcNow.AddMinutes(-6);
        h.Db.SaveChanges();
        Assert.False(await Eligible());
        binding.LastConfirmedAt = DateTime.UtcNow;
        machine.AgentCredentialHash = "rotated-agent-key-hash";
        h.Db.SaveChanges();
        Assert.False(await Eligible());
        binding.AgentCredentialHash = machine.AgentCredentialHash;
        target.Enabled = false;
        h.Db.SaveChanges();
        Assert.False(await Eligible());
        target.Enabled = true;
        target.WindowsServiceName = "DifferentService";
        target.UpdatedAt = DateTime.UtcNow.AddSeconds(1);
        h.Db.SaveChanges();
        Assert.False(await Eligible());
        target.TelemetryCredentialId = h.SeedCredential(incident.ProjectId).Id;
        h.Db.SaveChanges();
        Assert.False(await Eligible());
    }

    [Fact]
    public async Task ConfirmedProofIsBodyBoundAndSingleUse()
    {
        using var h = new TestHarness();
        h.EnsureProject();
        const string agentKey = "agent-proof-one-use-test-12345678901234567890";
        var agent = new AgentRegistrationService(h.Db, TimeProvider.System);
        var machineId = Guid.NewGuid();
        await agent.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId, HostName = "proof-host", OperatingSystem = "Windows",
            Architecture = "x64", AgentVersion = "1.0", AgentKey = agentKey,
            UserAgentKey = "user-proof-one-use-test-12345678901234567890"
        }, default);
        var credentials = new ProjectCredentialService(h.Db, Options.Create(new PlatformSecurityOptions()),
            TimeProvider.System, new PlatformAuditService(h.Db, TimeProvider.System,
                NullLogger<PlatformAuditService>.Instance));
        var key = (await credentials.CreateAsync(h.ProjectId, "proof-test", default))!;
        h.SeedRemediationTarget(machineId, key.Id, "proof-host", seedConfirmedBinding: false);
        var service = new MachineTelemetryBindingService(h.Db, agent, h.Targets,
            Options.Create(new WindowsRemediationOptions()));
        var body = JsonSerializer.SerializeToUtf8Bytes(new { projectId = h.ProjectId,
            service = h.Service, environment = h.Environment, value = 1 });
        var proof = await service.CreateProofAsync(h.ProjectId, h.Environment, h.Service,
            Convert.ToHexString(SHA256.HashData(body)), key.ApiKey, default);
        Assert.NotNull(proof);
        Assert.Null(await service.CreateProofAsync(Guid.NewGuid(), h.Environment, h.Service,
            Convert.ToHexString(SHA256.HashData(body)), key.ApiKey, default));
        Assert.False(await service.ConfirmProofAsync(proof!.Value, machineId, "wrong-agent-key", default));
        Assert.True(await service.ConfirmProofAsync(proof.Value, machineId, agentKey, default));
        Assert.False(await service.ConfirmProofAsync(proof.Value, machineId, agentKey, default));

        DefaultHttpContext Request(byte[] bytes)
        {
            var context = new DefaultHttpContext();
            context.Request.Body = new MemoryStream(bytes);
            context.Request.Headers["X-Kairon-API-Key"] = key.ApiKey;
            context.Request.Headers["X-Kairon-Machine-Proof"] = proof.Value.ToString();
            return context;
        }
        Assert.True((await service.ResolveAsync(Request([1]).Request, h.ProjectId,
            h.Environment, h.Service, default)).InvalidAgentProof);
        var accepted = await service.ResolveAsync(Request(body).Request, h.ProjectId,
            h.Environment, h.Service, default);
        Assert.False(accepted.InvalidAgentProof);
        Assert.Equal(machineId, accepted.MachineId);
        Assert.True((await service.ResolveAsync(Request(body).Request, h.ProjectId,
            h.Environment, h.Service, default)).InvalidAgentProof);
        Assert.Single(h.Db.SdkMachineBindings);

        var otherMachineId = Guid.NewGuid();
        const string otherAgentKey = "other-machine-proof-agent-12345678901234567890";
        await agent.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = otherMachineId, HostName = "other-host", OperatingSystem = "Windows",
            Architecture = "x64", AgentVersion = "1.0", AgentKey = otherAgentKey,
            UserAgentKey = "other-machine-proof-user-12345678901234567890"
        }, default);
        var second = await service.CreateProofAsync(h.ProjectId, h.Environment, h.Service,
            Convert.ToHexString(SHA256.HashData(body)), key.ApiKey, default);
        Assert.NotNull(second);
        Assert.False(await service.ConfirmProofAsync(second!.Value, otherMachineId, otherAgentKey, default));
    }

    [Fact]
    public async Task ExpiredAgentConfirmationNeverCreatesMachineScope()
    {
        using var h = new TestHarness();
        h.EnsureProject();
        var credentials = new ProjectCredentialService(h.Db, Options.Create(new PlatformSecurityOptions()),
            TimeProvider.System, new PlatformAuditService(h.Db, TimeProvider.System,
                NullLogger<PlatformAuditService>.Instance));
        var issued = (await credentials.CreateAsync(h.ProjectId, "expired-proof", default))!;
        var agents = new AgentRegistrationService(h.Db, TimeProvider.System);
        var machineId = Guid.NewGuid();
        const string agentKey = "expired-proof-agent-key-12345678901234567890";
        await agents.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId, HostName = "expiry-host", OperatingSystem = "Windows",
            Architecture = "x64", AgentVersion = "1.0", AgentKey = agentKey,
            UserAgentKey = "expired-proof-user-key-12345678901234567890"
        }, default);
        var binding = new MachineTelemetryBindingService(h.Db, agents, h.Targets,
            Options.Create(new WindowsRemediationOptions()));
        var proof = await binding.CreateProofAsync(h.ProjectId, h.Environment, h.Service,
            Convert.ToHexString(SHA256.HashData([1, 2, 3])), issued.ApiKey, default);
        Assert.NotNull(proof);
        h.Db.SdkMachineProofChallenges.Single(x => x.Id == proof).ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        h.Db.SaveChanges();
        Assert.False(await binding.ConfirmProofAsync(proof!.Value, machineId, agentKey, default));
        var request = new DefaultHttpContext();
        request.Request.Body = new MemoryStream([1, 2, 3]);
        request.Request.Headers["X-Kairon-API-Key"] = issued.ApiKey;
        request.Request.Headers["X-Kairon-Machine-Proof"] = proof.Value.ToString();
        Assert.True((await binding.ResolveAsync(request.Request, h.ProjectId, h.Environment,
            h.Service, default)).InvalidAgentProof);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("other-key", false)]
    [InlineData("other-machine", true)]
    [InlineData("other-service", false)]
    [InlineData("other-environment", false)]
    [InlineData("revoked", false)]
    public async Task MachineEvidenceRequiresAgentProofAndMatchingTarget(string scenario, bool machineScoped)
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident();
        var credentials = new ProjectCredentialService(h.Db, Options.Create(new PlatformSecurityOptions()), TimeProvider.System,
            new PlatformAuditService(h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance));
        var credential = (await credentials.CreateAsync(incident.ProjectId, "target", default))!;
        var other = (await credentials.CreateAsync(incident.ProjectId, "other", default))!;
        const string agentKey = "agent-key-for-proof-test-12345678901234567890";
        var agent = new AgentRegistrationService(h.Db, TimeProvider.System);
        var machineId = Guid.NewGuid();
        await agent.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId, HostName = "enrolled", OperatingSystem = "Windows",
            Architecture = "x64", AgentVersion = "1.0", AgentKey = agentKey,
            UserAgentKey = "user-key-for-proof-test-12345678901234567890"
        }, default);
        var machine = h.Db.Machines.Single(m => m.Id == machineId);
        h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName,
            environment: incident.Environment, service: incident.Service, seedConfirmedBinding: false);
        var binding = new MachineTelemetryBindingService(h.Db, agent, h.Targets,
            Options.Create(new WindowsRemediationOptions()));
        var controller = new TelemetryController(h.Db, null!, h.Queue, credentials,
            Options.Create(new PlatformSecurityOptions()), h.Targets, binding) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var key = scenario == "other-key" ? other.ApiKey : credential.ApiKey;
        controller.Request.Headers["X-Kairon-API-Key"] = key;
        if (scenario == "revoked") await credentials.RevokeAsync(incident.ProjectId, credential.Id, default);
        var dto = new MetricDto {
            ProjectId = incident.ProjectId, MachineId = scenario == "other-machine" ? Guid.NewGuid() : machine.Id,
            Environment = scenario == "other-environment" ? "Staging" : incident.Environment,
            Service = scenario == "other-service" ? "other" : incident.Service, RequestCount = 1
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        controller.Request.Body = new MemoryStream(body);
        if (scenario != "revoked")
        {
            var proof = await binding.CreateProofAsync(dto.ProjectId, dto.Environment, dto.Service!,
                Convert.ToHexString(SHA256.HashData(body)), key, default);
            Assert.NotNull(proof);
            Assert.True(await binding.ConfirmProofAsync(proof!.Value, machine.Id, agentKey, default));
            controller.Request.Headers["X-Kairon-Machine-Proof"] = proof.Value.ToString();
        }
        var result = await controller.CreateMetric(dto, default);
        if (scenario == "revoked")
        {
            Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Empty(h.Db.Metrics);
        }
        else
        {
            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(machineScoped ? machine.Id : null, h.Db.Metrics.Single().MachineId);
        }
    }
}
