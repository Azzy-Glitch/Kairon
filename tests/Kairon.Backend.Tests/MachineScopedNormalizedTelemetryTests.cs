using System.Security.Cryptography;
using System.Text.Json;
using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Orchestration;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Remediation.Tools;
using Kairon.Backend.Services.Verification;
using Kairon.Backend.Services.Evidence;
using Kairon.Backend.Services.Correlation;
using Kairon.Backend.Infrastructure;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class MachineScopedNormalizedTelemetryTests
{
    [Theory]
    [InlineData("python", "python-sdk")]
    [InlineData("dotnet", "dotnet-sdk")]
    public async Task AgentConfirmedSdkShapeReachesApprovedRestartAndResolution(string sdkType, string source)
    {
        using var h = new TestHarness();
        h.EnsureProject();
        const string agentKey = "normalized-agent-proof-key-1234567890123456";
        var agents = new AgentRegistrationService(h.Db, TimeProvider.System);
        var machineId = Guid.NewGuid();
        await agents.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId, HostName = "python-host", OperatingSystem = "Windows",
            Architecture = "x64", AgentVersion = "1.0", AgentKey = agentKey,
            UserAgentKey = "normalized-user-proof-key-12345678901234567"
        }, default);
        var platformAudit = new PlatformAuditService(h.Db, TimeProvider.System,
            NullLogger<PlatformAuditService>.Instance);
        var credentials = new ProjectCredentialService(h.Db, Options.Create(new PlatformSecurityOptions()),
            TimeProvider.System, platformAudit);
        var pairing = new SdkPairingService(h.Db, credentials, TimeProvider.System,
            Options.Create(new ProductOptions()), platformAudit, NullLogger<SdkPairingService>.Instance);
        var created = (await pairing.CreateAsync(h.ProjectId, sdkType, default))!;
        var paired = (await pairing.RedeemAsync(created.Code, sdkType, "1.1.0", default))!;
        Assert.True(await pairing.ConfirmAsync(created.PairingId, paired.ApiKey, default));
        var credentialId = h.Db.SdkPairingSessions.Single(x => x.Id == created.PairingId).IssuedCredentialId!.Value;
        h.SeedRemediationTarget(machineId, credentialId, "python-host", seedConfirmedBinding: false);
        var bindings = new MachineTelemetryBindingService(h.Db, agents, h.Targets,
            Options.Create(new WindowsRemediationOptions()));
        var telemetry = new PlatformTelemetryService(h.Db, h.Queue, TimeProvider.System);

        for (var i = 0; i < 3; i++)
        {
            await SendMetricAsync(h, bindings, telemetry, credentials, paired.ApiKey, agentKey,
                machineId, DateTime.UtcNow.AddSeconds(-20 + i * 5), 96, source);
        }

        Assert.Equal(3, h.Db.TelemetryReceipts.Count());
        Assert.All(h.Db.TelemetryReceipts, x => Assert.Equal(machineId, x.MachineId));
        Assert.All(h.Db.Metrics, x => Assert.Equal(machineId, x.MachineId));
        var signals = await h.CreateDetectionEngine().EvaluateAsync(h.ProjectId, h.Environment, h.Service);
        Assert.NotEmpty(signals);
        Assert.All(signals, x => Assert.Equal(machineId, x.MachineId));
        var correlated = await h.CreateCorrelationEngine().CorrelateAsync(signals);
        Assert.Contains(correlated, incident =>
            Kairon.Backend.Models.Sre.IncidentMachineScope.GetMachineId(incident) == machineId);
        var restart = new RestartServiceTool(h.Db, h.Targets, new UnusedScm(), h.WindowsServices);
        Assert.Contains(await Task.WhenAll(correlated.Select(async incident =>
            await restart.TargetFingerprintAsync(incident) is not null)), eligible => eligible);

        // Continue the same Agent-confirmed observations through the existing policy/approval/
        // Windows-service/verification pipeline. The fake SCM is a test double, not a production
        // remediation path; the app still has no remediation API or client-supplied MachineId.
        var scm = new RecordingScm();
        restart = new RestartServiceTool(h.Db, h.Targets, scm, h.WindowsServices);
        var registry = new RemediationToolRegistry([restart]);
        var policy = new RemediationPolicy(registry, Options.Create(h.Remediation),
            NullLogger<RemediationPolicy>.Instance);
        var executor = new RemediationExecutor(registry, policy, h.Audit, h.Db,
            Options.Create(h.Remediation), NullLogger<RemediationExecutor>.Instance);
        var verifier = new VerificationService(h.Db, h.Audit,
            new RemediationToolRegistryAccessor(registry), Options.Create(h.Verification),
            Options.Create(h.Detection), NullLogger<VerificationService>.Instance);
        var recovery = new AsyncBeforeVerification(verifier, () => SendMetricAsync(h, bindings,
            telemetry, credentials, paired.ApiKey, agentKey, machineId, DateTime.UtcNow, 20, source));
        var orchestrator = new IncidentOrchestrator(h.Db, h.CreateDetectionEngine(),
            h.CreateCorrelationEngine(), new EvidenceCollector(h.Db, registry,
                Options.Create(h.AiOptions), Options.Create(h.Detection),
                NullLogger<EvidenceCollector>.Instance), h.Ai, policy, registry, executor,
            recovery, h.Audit, new IncidentKeyGenerator(h.Db), h.Queue,
            Options.Create(h.AiOptions), Options.Create(h.Remediation),
            NullLogger<IncidentOrchestrator>.Instance);
        var aiResult = FakeAiService.DefaultResult();
        aiResult.Recommendations = [new AiRecommendationDto
        {
            Action = ServiceToolNames.RestartService, Reason = "Restart the degraded service",
            ExpectedOutcome = "CPU returns below threshold", RiskLevel = "medium"
        }];
        h.Ai.NextResult = aiResult;
        var scopedIncident = correlated.First(incident =>
            Kairon.Backend.Models.Sre.IncidentMachineScope.GetMachineId(incident) == machineId);
        await orchestrator.InvestigateAsync(scopedIncident.Id);
        Assert.Equal(IncidentStatus.AwaitingApproval, scopedIncident.Status);
        var action = Assert.Single(h.Db.RemediationActions.Where(a => a.IncidentId == scopedIncident.Id));
        Assert.Equal(RemediationStatus.AwaitingApproval, action.Status);
        Assert.Empty(scm.Changes);
        await orchestrator.ApproveAsync(scopedIncident.Id, action.Id, "operator-test", null);
        await orchestrator.ExecuteAndVerifyAsync(scopedIncident.Id, action.Id);
        Assert.Equal([false, true], scm.Changes);
        Assert.Equal(RemediationStatus.Executed, action.Status);
        Assert.Equal(VerificationStatus.Passed, scopedIncident.VerificationState);
        Assert.Equal(IncidentStatus.Resolved, scopedIncident.Status);
        Assert.NotNull(scopedIncident.ResolvedAt);
    }

    private static async Task SendMetricAsync(TestHarness h, MachineTelemetryBindingService bindings,
        PlatformTelemetryService telemetry, ProjectCredentialService credentials, string sdkKey,
        string agentKey, Guid machineId, DateTime timestamp, double cpu, string source)
    {
        var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        byte[] body;
        if (source == "dotnet-sdk")
        {
            // Exercise the actual .NET SDK formatter. A forged caller MachineId deliberately
            // cannot appear on this wire contract or influence the backend's assigned machine.
            var item = NormalizedTelemetryEvent.From(new MetricPayload
            {
                ProjectId = Guid.NewGuid(), MachineId = Guid.NewGuid(), Timestamp = timestamp,
                Application = "CornerShop", Service = h.Service, Environment = h.Environment,
                CpuPercent = cpu, RequestCount = 10
            }, new KaironOptions { ProjectId = h.ProjectId });
            body = JsonSerializer.SerializeToUtf8Bytes(new { Events = new[] { item } }, webJson);
        }
        else
        {
            var pythonShape = new NormalizedTelemetryBatchDto { Events = [new NormalizedTelemetryEventDto
            {
                EventId = Guid.NewGuid(), ProjectId = h.ProjectId, Timestamp = timestamp,
                EventType = "metric", Severity = "Information", Source = source,
                Application = "CornerShop", Service = h.Service, Environment = h.Environment,
                Runtime = "Python 3.14", ResourceMetrics = new ResourceTelemetryMetricsDto
                { CpuPercent = cpu, RequestCount = 10 }
            }] };
            body = JsonSerializer.SerializeToUtf8Bytes(pythonShape, webJson);
        }
        var batch = JsonSerializer.Deserialize<NormalizedTelemetryBatchDto>(body, webJson)!;
        Assert.Single(batch.Events);
        Assert.Equal(h.ProjectId, batch.Events[0].ProjectId);
        Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("events")[0]
            .TryGetProperty("machineId", out _));
        var proof = await bindings.CreateProofAsync(h.ProjectId, h.Environment, h.Service,
            Convert.ToHexString(SHA256.HashData(body)), sdkKey, default);
        Assert.NotNull(proof);
        Assert.True(await bindings.ConfirmProofAsync(proof!.Value, machineId, agentKey, default));
        var controller = new PlatformTelemetryController(telemetry, credentials, null!, bindings,
            Options.Create(new PlatformSecurityOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Body = new MemoryStream(body);
        controller.Request.Headers["X-Kairon-API-Key"] = sdkKey;
        controller.Request.Headers["X-Kairon-Machine-Proof"] = proof.Value.ToString();
        var result = await controller.Ingest(batch, default);
        Assert.IsType<OkObjectResult>(result.Result);
    }


    private sealed class AsyncBeforeVerification(IVerificationService inner, Func<Task> sendRecovery)
        : IVerificationService
    {
        public async Task<Kairon.Backend.Models.Sre.VerificationResult> VerifyAsync(
            Kairon.Backend.Models.Sre.SreIncident incident, RemediationAction action,
            CancellationToken cancellationToken = default)
        {
            await sendRecovery();
            return await inner.VerifyAsync(incident, action, cancellationToken);
        }
    }

    private sealed class RecordingScm : IWindowsServiceControl
    {
        private int _state = 4;
        public List<bool> Changes { get; } = [];
        public Task<int> QueryAsync(string host, string service, CancellationToken ct) => Task.FromResult(_state);
        public Task ChangeAsync(string host, string service, bool start, CancellationToken ct)
        {
            Changes.Add(start);
            _state = start ? 4 : 1;
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedScm : IWindowsServiceControl
    {
        public Task<int> QueryAsync(string host, string service, CancellationToken ct) => Task.FromResult(4);
        public Task ChangeAsync(string host, string service, bool start, CancellationToken ct) =>
            throw new InvalidOperationException("Test never executes the Windows SCM operation.");
    }
}
