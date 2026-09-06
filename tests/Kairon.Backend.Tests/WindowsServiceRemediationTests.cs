using Kairon.Backend.Configuration;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class WindowsServiceRemediationTests
{
    [Theory]
    [InlineData("StartService", 1, 4, true, 1)]
    [InlineData("StartService", 4, 4, true, 0)]
    [InlineData("StopService", 4, 1, true, 1)]
    [InlineData("StopService", 1, 1, true, 0)]
    [InlineData("RunHealthCheck", 4, 4, true, 0)]
    [InlineData("RunHealthCheck", 1, 1, false, 0)]
    [InlineData("RestartService", 1, 1, false, 0)]
    [InlineData("RestartService", 2, 2, false, 0)]
    public async Task ProductionOperationsRespectServiceState(string operation, int initial, int final, bool success, int calls)
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident(); incident.Environment = "Production";
        var machine = new Machine { HostName = "enrolled-host", OperatingSystem = "Windows", AgentCredentialHash = "test", LastSeenAt = DateTime.UtcNow };
        var credential = new ProjectApiCredential { ProjectId = incident.ProjectId, KeyHash = "test" };
        h.Db.Machines.Add(machine); h.Db.ProjectApiCredentials.Add(credential);
        incident.CorrelatedMetricsJson = SreJson.Serialize(new[] { new CorrelatedSignalSnapshot { MetricName = "cpu", MachineId = machine.Id } });
        h.Db.SaveChanges();
        var options = Options.Create(new WindowsRemediationOptions { Targets = [new WindowsServiceTarget {
            ProjectId = incident.ProjectId, Environment = incident.Environment, Service = incident.Service,
            MachineId = machine.Id, ExpectedHostName = machine.HostName, WindowsServiceName = "ScopedService",
            TelemetryCredentialId = credential.Id, AllowedOperations = [operation]
        }] });
        var scm = new Scm { State = initial };
        WindowsServiceTool tool = operation switch {
            "StartService" => new StartServiceTool(h.Db, options, scm),
            "StopService" => new StopServiceTool(h.Db, options, scm),
            "RunHealthCheck" => new ServiceHealthCheckTool(h.Db, options, scm),
            _ => new RestartServiceTool(h.Db, options, scm)
        };
        var result = await tool.ExecuteAsync(new RemediationToolContext {
            ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey,
            Environment = incident.Environment, Service = incident.Service, ActionKey = "ACT-states",
            Parameters = new Dictionary<string, string> { ["targetFingerprint"] = tool.TargetFingerprint(incident)! }
        });
        Assert.Equal(success, result.Success); Assert.Equal(final, scm.State); Assert.Equal(calls, scm.Calls.Count);
        Assert.All(scm.Calls, c => Assert.Equal((machine.HostName, "ScopedService"), (c.Host, c.Service)));
    }

    [Fact]
    public async Task VerifiedRecoveryExcludesOldFaultSamplesButAllowsANewRecurrence()
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident(IncidentStatus.Resolved);
        incident.VerificationState = VerificationStatus.Passed;
        incident.ResolvedAt = DateTime.UtcNow.AddSeconds(-20);
        incident.CorrelationKey = $"{h.ProjectId}|{h.Environment}|{h.Service}";
        h.Db.SaveChanges();
        for (var i = 0; i < 4; i++) h.SeedMetric(DateTime.UtcNow.AddSeconds(-60 + i * 5), cpu: 95);
        for (var i = 0; i < 4; i++) h.SeedMetric(DateTime.UtcNow.AddSeconds(-15 + i * 5), cpu: 20);
        var engine = h.CreateDetectionEngine();
        Assert.Empty(await engine.EvaluateAsync(h.ProjectId, h.Environment, h.Service));
        for (var i = 0; i < 4; i++) h.SeedMetric(DateTime.UtcNow.AddSeconds(-15 + i * 5), cpu: 95);
        Assert.NotEmpty(await engine.EvaluateAsync(h.ProjectId, h.Environment, h.Service));
    }

    [Fact]
    public async Task TargetedDetectionCannotCorrelateAnotherMachinesTelemetry()
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident();
        var machine = Guid.NewGuid();
        var target = new WindowsServiceTarget { ProjectId = h.ProjectId, Environment = h.Environment, Service = h.Service, MachineId = machine };
        for (var i = 0; i < 4; i++) {
            var metric = h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 5), cpu: 96);
            metric.MachineId = Guid.NewGuid();
        }
        h.Db.SaveChanges();
        var engine = new Kairon.Backend.Services.Detection.DetectionEngine(h.Db, h.AllRules(),
            new Kairon.Backend.Services.Detection.InMemoryDetectionCooldownStore(), Options.Create(h.Detection),
            NullLogger<Kairon.Backend.Services.Detection.DetectionEngine>.Instance, Options.Create(new WindowsRemediationOptions { Targets = [target] }));
        Assert.Empty(await engine.EvaluateAsync(h.ProjectId, h.Environment, h.Service));
        foreach (var metric in h.Db.Metrics) metric.MachineId = machine;
        h.Db.SaveChanges();
        var signals = await engine.EvaluateAsync(h.ProjectId, h.Environment, h.Service);
        Assert.NotEmpty(signals);
        Assert.All(signals, s => Assert.Equal(machine, s.MachineId));
        var correlated = await h.CreateCorrelationEngine().CorrelateAsync(signals);
        Assert.All(correlated, i => Assert.Equal(machine, IncidentMachineScope.GetMachineId(i)));
        Assert.DoesNotContain(correlated, i => i.Id == incident.Id);
    }

    private sealed class Scm : IWindowsServiceControl {
        public int State = 4;
        public List<(string Host, string Service, bool Start)> Calls = [];
        public Task<int> QueryAsync(string host, string service, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(State); }
        public Task ChangeAsync(string host, string service, bool start, CancellationToken ct) {
            ct.ThrowIfCancellationRequested(); Calls.Add((host, service, start)); State = start ? 4 : 1; return Task.CompletedTask;
        }
    }
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task ExactApprovedTargetCanRestartAndChangedTargetCannot(string environment) {
        using var h = new TestHarness();
        var incident = h.SeedIncident(); incident.Environment = environment;
        var machine = new Machine { Id = Guid.NewGuid(), HostName = "enrolled-host", OperatingSystem = "Windows", AgentCredentialHash = "enrollment", LastSeenAt = DateTime.UtcNow };
        h.Db.Machines.Add(machine);
        var credential = new ProjectApiCredential { ProjectId = incident.ProjectId, KeyHash = "test-hash" };
        h.Db.ProjectApiCredentials.Add(credential);
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        h.Db.SaveChanges();
        var target = new WindowsServiceTarget { ProjectId = incident.ProjectId, Environment = environment, Service = incident.Service, MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName, WindowsServiceName = "ScopedService", AllowedOperations = [ServiceToolNames.RestartService] };
        var options = Options.Create(new WindowsRemediationOptions { Targets = [target] });
        var scm = new Scm();
        var tool = new RestartServiceTool(h.Db, options, scm);
        var registry = new RemediationToolRegistry([tool]);
        var policy = new RemediationPolicy(registry, Options.Create(new RemediationOptions()), NullLogger<RemediationPolicy>.Instance);
        var parameters = new Dictionary<string, string> { ["targetFingerprint"] = tool.TargetFingerprint(incident)! };
        var action = new RemediationAction { IncidentId = incident.Id, ActionType = tool.Name, Status = RemediationStatus.Proposed, ParametersJson = SreJson.Serialize(parameters) };
        Assert.False(policy.ValidateExecution(incident, action).Allowed);
        action.Status = RemediationStatus.Approved;
        Assert.True(policy.ValidateExecution(incident, action).Allowed);
        var result = await tool.ExecuteAsync(new RemediationToolContext { ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey, ActionKey = "ACT-test", Service = incident.Service, Environment = environment, Parameters = parameters });
        Assert.True(result.Success);
        Assert.Equal(new[] { ("enrolled-host", "ScopedService", false), ("enrolled-host", "ScopedService", true) }, scm.Calls);
        target.WindowsServiceName = "AnotherService";
        Assert.Equal("target-changed", policy.ValidateExecution(incident, action).Code);
        Assert.False((await tool.ExecuteAsync(new RemediationToolContext { ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey, ActionKey = "ACT-test", Service = incident.Service, Environment = environment, Parameters = parameters })).Success);
        Assert.Equal(2, scm.Calls.Count);
        machine.LastSeenAt = DateTime.UtcNow.AddMinutes(-10);
        h.Db.SaveChanges();
        Assert.Null(tool.TargetFingerprint(incident));
    }
    [Theory]
    [InlineData("Demo")]
    [InlineData("Hackathon")]
    public void RetiredEnvironmentCannotBeEnabledByAllowlist(string environment) {
        using var h = new TestHarness();
        h.Remediation.AllowedEnvironments.Add(environment);
        var incident = h.SeedIncident(); incident.Environment = environment;
        Assert.Equal("environment-not-allowed", h.Policy.ValidateProposal(incident, DemoToolNames.RunHealthCheck, RiskLevel.Low).Code);
    }

    [Theory]
    [InlineData("matching", VerificationStatus.Passed)]
    [InlineData("other-machine", VerificationStatus.Inconclusive)]
    [InlineData("other-service", VerificationStatus.Inconclusive)]
    [InlineData("no-machine", VerificationStatus.Inconclusive)]
    [InlineData("future", VerificationStatus.Inconclusive)]
    [InlineData("missing-signal", VerificationStatus.Failed)]
    [InlineData("stopped", VerificationStatus.Inconclusive)]
    public async Task RecoveryRequiresFreshExactScopeAndRunningService(string scenario, VerificationStatus expected)
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident(IncidentStatus.Verifying);
        var machine = new Machine { Id = Guid.NewGuid(), HostName = "enrolled-host", OperatingSystem = "Windows", AgentCredentialHash = "enrollment", LastSeenAt = DateTime.UtcNow };
        h.Db.Machines.Add(machine);
        var credential = new ProjectApiCredential { ProjectId = incident.ProjectId, KeyHash = "test-hash" };
        h.Db.ProjectApiCredentials.Add(credential);
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        h.Db.SaveChanges();
        var target = new WindowsServiceTarget { ProjectId = incident.ProjectId, Environment = incident.Environment, Service = incident.Service, MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName, WindowsServiceName = "ScopedService", AllowedOperations = [ServiceToolNames.RestartService] };
        var scm = new Scm { State = scenario == "stopped" ? 1 : 4 };
        var tool = new RestartServiceTool(h.Db, Options.Create(new WindowsRemediationOptions { Targets = [target] }), scm);
        var action = new RemediationAction { IncidentId = incident.Id, ActionKey = "ACT-scope", ActionType = tool.Name, Status = RemediationStatus.Executed, CompletedAt = DateTime.UtcNow.AddSeconds(-20), ParametersJson = SreJson.Serialize(new Dictionary<string, string> { ["targetFingerprint"] = tool.TargetFingerprint(incident)! }) };
        incident.Actions.Add(action); h.Db.RemediationActions.Add(action);
        var before = h.SeedMetric(DateTime.UtcNow.AddSeconds(-30), cpu: 95, retries: 50);
        before.MachineId = machine.Id;
        var after = h.SeedMetric(DateTime.UtcNow.AddSeconds(scenario == "future" ? 20 : -2), cpu: 20, retries: scenario == "missing-signal" ? null : 0);
        after.MachineId = scenario == "no-machine" ? null : scenario == "other-machine" ? Guid.NewGuid() : machine.Id;
        if (scenario == "other-service") after.Service = "OtherService";
        h.Db.SaveChanges();
        var verifier = new Kairon.Backend.Services.Verification.VerificationService(h.Db, h.Audit,
            new Kairon.Backend.Services.Verification.RemediationToolRegistryAccessor(new RemediationToolRegistry([tool])),
            Options.Create(h.Verification), Options.Create(h.Detection), NullLogger<Kairon.Backend.Services.Verification.VerificationService>.Instance);
        var result = await verifier.VerifyAsync(incident, action);
        Assert.Equal(expected, result.Status);
        var audit = h.Db.IncidentEvents.Single(e => e.EventType == IncidentEventTypes.Verified);
        Assert.Contains(machine.Id.ToString(), audit.DataJson);
    }
}
