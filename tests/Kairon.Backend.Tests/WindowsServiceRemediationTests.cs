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
        var machine = h.SeedMachine(agentCredentialHash: "test");
        var credential = h.SeedCredential(incident.ProjectId, "test");
        incident.CorrelatedMetricsJson = SreJson.Serialize(new[] { new CorrelatedSignalSnapshot { MetricName = "cpu", MachineId = machine.Id } });
        h.Db.SaveChanges();
        h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName,
            environment: incident.Environment, service: incident.Service, allowedOperations: [operation]);
        var scm = new Scm { State = initial };
        WindowsServiceTool tool = operation switch {
            "StartService" => new StartServiceTool(h.Db, h.Targets, scm),
            "StopService" => new StopServiceTool(h.Db, h.Targets, scm),
            "RunHealthCheck" => new ServiceHealthCheckTool(h.Db, h.Targets, scm),
            _ => new RestartServiceTool(h.Db, h.Targets, scm)
        };
        var result = await tool.ExecuteAsync(new RemediationToolContext {
            ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey,
            Environment = incident.Environment, Service = incident.Service, ActionKey = "ACT-states",
            Parameters = new Dictionary<string, string> { ["targetFingerprint"] = (await tool.TargetFingerprintAsync(incident))! }
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
        for (var i = 0; i < 4; i++) {
            var metric = h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 5), cpu: 96);
            metric.MachineId = Guid.NewGuid();
        }
        h.Db.SaveChanges();
        h.SeedRemediationTarget(machine, Guid.NewGuid(), "any-host");
        var engine = h.CreateDetectionEngine();
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
        public Queue<int> QueryStates = new();
        public bool DenyQuery;
        public List<(string Host, string Service, bool Start)> Calls = [];
        public Task<int> QueryAsync(string host, string service, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (DenyQuery) throw new UnauthorizedAccessException(); return Task.FromResult(QueryStates.Count > 0 ? QueryStates.Dequeue() : State); }
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
        var machine = h.SeedMachine();
        var credential = h.SeedCredential(incident.ProjectId);
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        h.Db.SaveChanges();
        var target = h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName,
            environment: environment, service: incident.Service, allowedOperations: [ServiceToolNames.RestartService]);
        var scm = new Scm();
        var tool = new RestartServiceTool(h.Db, h.Targets, scm);
        var registry = new RemediationToolRegistry([tool]);
        var policy = new RemediationPolicy(registry, Options.Create(new RemediationOptions()), NullLogger<RemediationPolicy>.Instance);
        var parameters = new Dictionary<string, string> { ["targetFingerprint"] = (await tool.TargetFingerprintAsync(incident))! };
        var action = new RemediationAction { IncidentId = incident.Id, ActionType = tool.Name, Status = RemediationStatus.Proposed, ParametersJson = SreJson.Serialize(parameters) };
        Assert.False((await policy.ValidateExecutionAsync(incident, action)).Allowed);
        action.Status = RemediationStatus.Approved;
        Assert.True((await policy.ValidateExecutionAsync(incident, action)).Allowed);
        var result = await tool.ExecuteAsync(new RemediationToolContext { ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey, ActionKey = "ACT-test", Service = incident.Service, Environment = environment, Parameters = parameters });
        Assert.True(result.Success);
        Assert.Equal(new[] { ("enrolled-host", "ScopedService", false), ("enrolled-host", "ScopedService", true) }, scm.Calls);
        target.WindowsServiceName = "AnotherService";
        h.Db.SaveChanges();
        Assert.Equal("target-changed", (await policy.ValidateExecutionAsync(incident, action)).Code);
        Assert.False((await tool.ExecuteAsync(new RemediationToolContext { ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey, ActionKey = "ACT-test", Service = incident.Service, Environment = environment, Parameters = parameters })).Success);
        Assert.Equal(2, scm.Calls.Count);
        machine.LastSeenAt = DateTime.UtcNow.AddMinutes(-10);
        h.Db.SaveChanges();
        Assert.Null(await tool.TargetFingerprintAsync(incident));
    }
    [Theory]
    [InlineData("Demo")]
    [InlineData("Hackathon")]
    public async Task RetiredEnvironmentCannotBeEnabledByAllowlist(string environment) {
        using var h = new TestHarness();
        h.Remediation.AllowedEnvironments.Add(environment);
        var incident = h.SeedIncident(); incident.Environment = environment;
        Assert.Equal("environment-not-allowed", (await h.Policy.ValidateProposalAsync(incident, DemoToolNames.RunHealthCheck, RiskLevel.Low)).Code);
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
        var machine = h.SeedMachine();
        var credential = h.SeedCredential(incident.ProjectId);
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        h.Db.SaveChanges();
        h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName,
            environment: incident.Environment, service: incident.Service, allowedOperations: [ServiceToolNames.RestartService]);
        var scm = new Scm { State = scenario == "stopped" ? 1 : 4 };
        var tool = new RestartServiceTool(h.Db, h.Targets, scm);
        var action = new RemediationAction { IncidentId = incident.Id, ActionKey = "ACT-scope", ActionType = tool.Name, Status = RemediationStatus.Executed, CompletedAt = DateTime.UtcNow.AddSeconds(-20), ParametersJson = SreJson.Serialize(new Dictionary<string, string> { ["targetFingerprint"] = (await tool.TargetFingerprintAsync(incident))! }) };
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

    [Theory]
    [InlineData("stopped", VerificationStatus.Passed)]
    [InlineData("running", VerificationStatus.Inconclusive)]
    [InlineData("old-heartbeat", VerificationStatus.Inconclusive)]
    [InlineData("changed-target", VerificationStatus.Inconclusive)]
    [InlineData("restarted", VerificationStatus.Inconclusive)]
    [InlineData("access-denied", VerificationStatus.Inconclusive)]
    [InlineData("missing-binding", VerificationStatus.Inconclusive)]
    [InlineData("missing-completion", VerificationStatus.Inconclusive)]
    [InlineData("future-completion", VerificationStatus.Inconclusive)]
    public async Task StopVerificationRequiresStableStoppedStateAndIndependentFreshMachineEvidence(string scenario, VerificationStatus expected)
    {
        using var h = new TestHarness();
        var incident = h.SeedIncident(IncidentStatus.Verifying);
        var machine = h.SeedMachine();
        var credential = h.SeedCredential(incident.ProjectId);
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        h.Db.SaveChanges();
        var target = h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName,
            environment: incident.Environment, service: incident.Service, allowedOperations: [ServiceToolNames.StopService]);
        var scm = new Scm { State = scenario == "running" ? 4 : 1 };
        var tool = new StopServiceTool(h.Db, h.Targets, scm);
        var action = new RemediationAction { IncidentId = incident.Id, ActionKey = "ACT-scope", ActionType = tool.Name, Status = RemediationStatus.Executed, CompletedAt = DateTime.UtcNow.AddSeconds(-20), ParametersJson = SreJson.Serialize(new Dictionary<string, string> { ["targetFingerprint"] = (await tool.TargetFingerprintAsync(incident))! }) };
        incident.Actions.Add(action); h.Db.RemediationActions.Add(action);

        if (scenario == "old-heartbeat") machine.LastSeenAt = action.CompletedAt!.Value.AddSeconds(-1);
        if (scenario == "changed-target") target.WindowsServiceName = "AnotherService";
        if (scenario == "restarted") { scm.QueryStates.Enqueue(1); scm.QueryStates.Enqueue(4); }
        if (scenario == "access-denied") scm.DenyQuery = true;
        if (scenario == "missing-binding") action.ParametersJson = "{}";
        if (scenario == "missing-completion") action.CompletedAt = null;
        if (scenario == "future-completion") action.CompletedAt = DateTime.UtcNow.AddMinutes(1);
        h.Db.SaveChanges();
        var verifier = new Kairon.Backend.Services.Verification.VerificationService(h.Db, h.Audit,
            new Kairon.Backend.Services.Verification.RemediationToolRegistryAccessor(new RemediationToolRegistry([tool])),
            Options.Create(h.Verification), Options.Create(h.Detection), NullLogger<Kairon.Backend.Services.Verification.VerificationService>.Instance);
        var result = await verifier.VerifyAsync(incident, action);
        Assert.Equal(expected, result.Status);
        Assert.Empty(h.Db.Metrics); // A deliberately stopped workload cannot emit recovery metrics.
        Assert.Empty(scm.Calls); // Verification is read-only.
        var audit = h.Db.IncidentEvents.Single(e => e.EventType == IncidentEventTypes.Verified);
        Assert.Contains("Stopped", audit.DataJson);
        Assert.Contains(machine.Id.ToString(), audit.DataJson);
        if (expected == VerificationStatus.Passed) Assert.Contains("availability is not restored", result.Summary);
    }

    private sealed class MutatingScm : IWindowsServiceControl {
        public int State = 1;
        public List<(string Host, string Service, bool Start)> Calls = [];
        public Action? OnQuery;
        public Task<int> QueryAsync(string host, string service, CancellationToken ct) {
            var callback = OnQuery;
            OnQuery = null; // only ever mutate once, however many times QueryAsync is called
            callback?.Invoke();
            return Task.FromResult(State);
        }
        public Task ChangeAsync(string host, string service, bool start, CancellationToken ct) {
            Calls.Add((host, service, start)); State = start ? 4 : 1; return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ATargetDisabledDuringTheScmQueryIsCaughtBeforeTheIrreversibleChangeCall()
    {
        // P2-11 (TOCTOU): the target is re-resolved and re-fingerprinted immediately before each
        // irreversible ChangeAsync call, not just once at the top of ExecuteAsync. This simulates
        // an operator disabling the target during the SCM query's own round-trip - after the
        // lock-acquisition-time check already passed, but before the state-changing SCM call.
        using var h = new TestHarness();
        var incident = h.SeedIncident(); incident.Environment = "Production";
        var machine = h.SeedMachine();
        var credential = h.SeedCredential(incident.ProjectId);
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        h.Db.SaveChanges();
        var target = h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName,
            environment: incident.Environment, service: incident.Service, allowedOperations: [ServiceToolNames.StartService]);

        var scm = new MutatingScm { State = 1 };
        var tool = new StartServiceTool(h.Db, h.Targets, scm);
        var fingerprint = (await tool.TargetFingerprintAsync(incident))!;
        scm.OnQuery = () => { target.Enabled = false; h.Db.SaveChanges(); };

        var result = await tool.ExecuteAsync(new RemediationToolContext {
            ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey,
            Environment = incident.Environment, Service = incident.Service, ActionKey = "ACT-toctou",
            Parameters = new Dictionary<string, string> { ["targetFingerprint"] = fingerprint }
        });

        Assert.False(result.Success);
        Assert.Empty(scm.Calls); // the irreversible SCM state change was never issued
    }

    [Fact]
    public async Task TargetFingerprintFailsSafelyRatherThanThrowingWhenTheMachineDisappearsMidResolution()
    {
        // Machines are never physically deleted through any endpoint in this system today, but
        // TargetFingerprint must not assume that forever: a target whose Machine vanished between
        // resolution steps (or via any future code path) must be treated exactly like any other
        // resolution failure in this method - a null fingerprint, not an unhandled exception.
        using var h = new TestHarness();
        var incident = h.SeedIncident();
        var machine = h.SeedMachine();
        var credential = h.SeedCredential(h.ProjectId);
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        h.Db.SaveChanges();
        h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName, allowedOperations: [ServiceToolNames.RestartService]);

        var tool = new RestartServiceTool(h.Db, h.Targets, new Scm());
        Assert.NotNull(await tool.TargetFingerprintAsync(incident)); // sanity: resolves fine while the machine exists

        h.Db.Machines.Remove(machine);
        h.Db.SaveChanges();

        Assert.Null(await tool.TargetFingerprintAsync(incident));
    }
}
