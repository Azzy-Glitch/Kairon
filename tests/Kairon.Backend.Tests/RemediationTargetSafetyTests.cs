using Kairon.Backend.DTOs;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>Local-only execution, service eligibility, operator-confirmed service identity,
/// Windows-permission pre-flight and classified failures. Every refusal here must fail closed
/// before any SCM state change is issued.</summary>
public sealed class RemediationTargetSafetyTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private RemediationTargetManagementService Service() =>
        new(_h.Db, new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System,
            null, _h.WindowsServices, _h.WindowsServices);

    private CreateRemediationTargetRequest Request(Machine machine, ProjectApiCredential credential, params string[] operations) => new()
    {
        ProjectId = _h.ProjectId, Environment = _h.Environment, Service = _h.Service, MachineId = machine.Id,
        TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName, WindowsServiceName = "ScopedService",
        AllowedOperations = operations.Length == 0 ? [ServiceToolNames.RestartService] : operations.ToList()
    };

    // --- Eligibility policy ---

    [Theory]
    [InlineData("Kairon.Agent", 0x10, @"C:\Program Files\Kairon\agent\Kairon.Agent.exe", false, ServiceEligibility.KaironInfrastructure)]
    [InlineData("kairon.backend", 0x10, @"C:\Apps\x.exe", false, ServiceEligibility.KaironInfrastructure)]
    [InlineData("W32Time", 0x20, @"C:\WINDOWS\system32\svchost.exe -k LocalService", false, ServiceEligibility.NotApplicationService)]
    [InlineData("SomeDriver", 0x1, @"\SystemRoot\System32\drivers\x.sys", false, ServiceEligibility.NotApplicationService)]
    [InlineData("UserSvc", 0x50, @"C:\Apps\u.exe", false, ServiceEligibility.NotApplicationService)]
    [InlineData("Spooler", 0x10, @"C:\WINDOWS\System32\spoolsv.exe", false, ServiceEligibility.WindowsComponent)]
    [InlineData("Protected", 0x10, @"C:\Apps\p.exe", true, ServiceEligibility.Protected)]
    [InlineData("OrdersService", 0x10, "\"C:\\Program Files\\Orders\\orders.exe\" --service", false, ServiceEligibility.Eligible)]
    public void EligibilityAllowsOnlyStandAloneApplicationServices(string name, int type, string image, bool isProtected, ServiceEligibility expected) =>
        Assert.Equal(expected, ServiceEligibilityPolicy.Evaluate(name, type, image, isProtected, systemRoot: @"C:\WINDOWS"));

    [Fact]
    public void IdentityChangesWhenTheExecutableOrAccountChanges()
    {
        var original = ServiceEligibilityPolicy.IdentityHash(0x10, @"C:\Apps\orders.exe", "LocalSystem");
        Assert.Equal(original, ServiceEligibilityPolicy.IdentityHash(0x10, @"c:\apps\ORDERS.exe ", "localsystem"));
        Assert.NotEqual(original, ServiceEligibilityPolicy.IdentityHash(0x10, @"C:\Temp\orders.exe", "LocalSystem"));
        Assert.NotEqual(original, ServiceEligibilityPolicy.IdentityHash(0x10, @"C:\Apps\orders.exe", @".\attacker"));
    }

    [Theory]
    [InlineData(5, "PermissionMissing")]
    [InlineData(1060, "ServiceMissing")]
    [InlineData(1051, "DependentServicesRunning")]
    [InlineData(1053, "ServiceTimeout")]
    [InlineData(1058, "ServiceDisabled")]
    [InlineData(42, "ScmFailed")]
    public void ScmExitCodesAreClassified(int exitCode, string classification) =>
        Assert.StartsWith(classification + ":", WindowsServiceControl.DescribeScmFailure("start", exitCode));

    // --- Resolver: fail closed with a precise reason ---

    private (SreIncident Incident, Machine Machine, ProjectApiCredential Credential) SeedScope()
    {
        var incident = _h.SeedIncident();
        var machine = _h.SeedMachine(agentCredentialHash: "test");
        var credential = _h.SeedCredential(incident.ProjectId, "test");
        incident.CorrelatedMetricsJson = SreJson.Serialize(new[] { new CorrelatedSignalSnapshot { MetricName = "cpu", MachineId = machine.Id } });
        _h.Db.SaveChanges();
        return (incident, machine, credential);
    }

    [Fact]
    public async Task OperationNotAllowedByTheTargetNeverResolves()
    {
        var (_, machine, credential) = SeedScope();
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName, allowedOperations: [ServiceToolNames.RunHealthCheck]);

        Assert.Equal(TargetReadiness.OperationNotAllowed, (await _h.Targets.EvaluateTargetAsync(target, ServiceToolNames.StopService)).Readiness);
        Assert.Null(await _h.Targets.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.StopService));
        Assert.NotNull(await _h.Targets.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.RunHealthCheck));
    }

    [Fact]
    public async Task RemoteHostTargetsNeverResolve()
    {
        var (_, machine, credential) = SeedScope();
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);
        _h.WindowsServices.LocalHost = _ => false;

        Assert.Equal(TargetReadiness.RemoteNotSupported, (await _h.Targets.EvaluateTargetAsync(target, ServiceToolNames.RestartService)).Readiness);
        Assert.Null(await _h.Targets.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.RestartService));
    }

    [Fact]
    public void RealLocalMachineBoundaryOnlyAcceptsThisHost()
    {
        Assert.True(LocalMachine.Instance.IsLocal(Environment.MachineName.ToLowerInvariant()));
        Assert.False(LocalMachine.Instance.IsLocal("definitely-not-" + Environment.MachineName));
        Assert.False(LocalMachine.Instance.IsLocal(""));
    }

    [Fact]
    public async Task UnconfirmedServiceIdentityNeverResolves()
    {
        var (_, machine, credential) = SeedScope();
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);
        target.ServiceIdentityHash = null;
        _h.Db.SaveChanges();

        Assert.Equal(TargetReadiness.StaleTarget, (await _h.Targets.EvaluateTargetAsync(target, ServiceToolNames.RestartService)).Readiness);
        Assert.Null(await _h.Targets.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.RestartService));
    }

    // --- Execution: live service re-check before any SCM call ---

    private sealed class RecordingScm : IWindowsServiceControl
    {
        public int State = 4;
        public int Queries;
        public List<bool> Changes { get; } = [];
        public Task<int> QueryAsync(string host, string service, CancellationToken ct) { Queries++; return Task.FromResult(State); }
        public Task ChangeAsync(string host, string service, bool start, CancellationToken ct) { Changes.Add(start); State = start ? 4 : 1; return Task.CompletedTask; }
    }

    private async Task<(RemediationToolResult Result, RecordingScm Scm)> ExecuteRestartAsync(Action<FakeWindowsServices> mutateAfterApproval)
    {
        var (incident, machine, credential) = SeedScope();
        _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);
        var scm = new RecordingScm();
        var tool = new RestartServiceTool(_h.Db, _h.Targets, scm, _h.WindowsServices);
        var fingerprint = await tool.TargetFingerprintAsync(incident);
        Assert.NotNull(fingerprint);
        mutateAfterApproval(_h.WindowsServices);
        var result = await tool.ExecuteAsync(new RemediationToolContext
        {
            ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey,
            Environment = incident.Environment, Service = incident.Service, ActionKey = "ACT-safety",
            Parameters = new Dictionary<string, string> { ["targetFingerprint"] = fingerprint! }
        });
        return (result, scm);
    }

    [Fact]
    public async Task ApprovedRestartSucceedsAgainstTheConfirmedService()
    {
        var (result, scm) = await ExecuteRestartAsync(_ => { });
        Assert.True(result.Success);
        Assert.Equal([false, true], scm.Changes);
    }

    [Fact]
    public async Task ServiceRecreatedAroundAnotherExecutableIsRefusedBeforeAnyScmCall()
    {
        var (result, scm) = await ExecuteRestartAsync(f => f.Overrides["ScopedService"] = FakeWindowsServices.Eligible("ScopedService", identity: new string('F', 64)));
        Assert.False(result.Success);
        Assert.StartsWith("ServiceIdentityChanged:", result.Error);
        Assert.Equal(0, scm.Queries);
        Assert.Empty(scm.Changes);
    }

    [Fact]
    public async Task MissingWindowsPermissionIsReportedAsSuchBeforeAnyScmCall()
    {
        var (result, scm) = await ExecuteRestartAsync(f => f.Overrides["ScopedService"] = FakeWindowsServices.Eligible("ScopedService") with { CanStop = false });
        Assert.False(result.Success);
        Assert.StartsWith("PermissionMissing:", result.Error);
        Assert.Contains("Stop", result.Error);
        Assert.Equal(0, scm.Queries);
        Assert.Empty(scm.Changes);
    }

    [Fact]
    public async Task DeletedServiceIsReportedAsMissing()
    {
        var (result, scm) = await ExecuteRestartAsync(f => f.Overrides["ScopedService"] = new WindowsServiceProbe { ServiceName = "ScopedService", Exists = false });
        Assert.StartsWith("ServiceMissing:", result.Error);
        Assert.Empty(scm.Changes);
    }

    [Fact]
    public async Task DenylistedServiceIsRefused()
    {
        var (result, scm) = await ExecuteRestartAsync(f => f.Overrides["ScopedService"] =
            FakeWindowsServices.Eligible("ScopedService") with { Eligibility = ServiceEligibility.WindowsComponent });
        Assert.StartsWith("Denylisted:", result.Error);
        Assert.Empty(scm.Changes);
    }

    // --- Management: enabling requires a passing Windows pre-flight and binds the identity ---

    [Fact]
    public async Task EnablingCapturesTheLiveServiceIdentity()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var result = await Service().CreateAsync(Request(machine, credential), "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, result.Outcome);
        Assert.Equal(FakeWindowsServices.DefaultIdentity, _h.Db.RemediationTargets.Single().ServiceIdentityHash);
        Assert.True(result.Target!.ServiceIdentityConfirmed);
    }

    [Theory]
    [InlineData("permission", "permission")]
    [InlineData("missing", "does not exist")]
    [InlineData("denylisted", "Windows component")]
    [InlineData("remote", "Remote remediation is not supported")]
    [InlineData("platform", "requires a Windows backend")]
    public async Task EnablingIsRefusedUntilTheTargetCanSafelyExecute(string scenario, string expectedError)
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        switch (scenario)
        {
            case "permission": _h.WindowsServices.Overrides["ScopedService"] = FakeWindowsServices.Eligible("ScopedService") with { CanStart = false }; break;
            case "missing": _h.WindowsServices.Overrides["ScopedService"] = new WindowsServiceProbe { ServiceName = "ScopedService" }; break;
            case "denylisted": _h.WindowsServices.Overrides["ScopedService"] = FakeWindowsServices.Eligible("ScopedService") with { Eligibility = ServiceEligibility.WindowsComponent }; break;
            case "remote": _h.WindowsServices.LocalHost = _ => false; break;
            case "platform": _h.WindowsServices.IsSupported = false; break;
        }

        var result = await Service().CreateAsync(Request(machine, credential), "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(expectedError, result.Error);
        Assert.Empty(_h.Db.RemediationTargets);
    }

    [Fact]
    public async Task HealthCheckOnlyTargetsNeedOnlyTheQueryRight()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        _h.WindowsServices.Overrides["ScopedService"] = FakeWindowsServices.Eligible("ScopedService") with { CanStart = false, CanStop = false };

        var result = await Service().CreateAsync(Request(machine, credential, ServiceToolNames.RunHealthCheck), "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task DisabledTargetsMayBeStagedButEnablingRunsThePreflight()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        _h.WindowsServices.Overrides["ScopedService"] = FakeWindowsServices.Eligible("ScopedService") with { CanStop = false };
        var request = Request(machine, credential);
        request.Enabled = false;
        var service = Service();

        var created = await service.CreateAsync(request, "op", default);
        Assert.Equal(RemediationTargetOperationOutcome.Success, created.Outcome);
        Assert.Equal("Disabled", created.Target!.Readiness);
        Assert.Null(_h.Db.RemediationTargets.Single().ServiceIdentityHash);

        var refused = await service.SetEnabledAsync(created.Target.Id, true, "op", default);
        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed, refused.Outcome);

        _h.WindowsServices.Overrides.Clear();
        var enabled = await service.SetEnabledAsync(created.Target.Id, true, "op", default);
        Assert.Equal(RemediationTargetOperationOutcome.Success, enabled.Outcome);
        Assert.Equal(FakeWindowsServices.DefaultIdentity, _h.Db.RemediationTargets.Single().ServiceIdentityHash);
    }

    [Fact]
    public async Task PreflightReportsEachCheckAndTheExactMissingRights()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        _h.WindowsServices.Overrides["ScopedService"] = FakeWindowsServices.Eligible("ScopedService") with { CanStart = false, CanStop = false };

        var preflight = await Service().PreflightAsync(Request(machine, credential, ServiceToolNames.RestartService), default);

        Assert.False(preflight.CanEnable);
        Assert.Equal(nameof(TargetReadiness.PermissionMissing), preflight.Readiness);
        Assert.Equal(["Query", "Start", "Stop"], preflight.RequiredRights);
        Assert.Equal(["Start", "Stop"], preflight.MissingRights);
        Assert.Contains(preflight.Checks, c => c.Key == "right-query" && c.Passed);
        Assert.Contains(preflight.Checks, c => c.Key == "right-stop" && !c.Passed && c.Blocking);
        Assert.Empty(_h.Db.RemediationTargets);
    }

    [Fact]
    public async Task SavedTargetReadinessReportsAwaitingAgentConfirmationUntilAppTelemetryIsConfirmed()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var created = await Service().CreateAsync(Request(machine, credential), "op", default);

        var preflight = await Service().PreflightAsync(created.Target!.Id, default);

        Assert.True(preflight!.CanEnable);
        Assert.Equal(nameof(TargetReadiness.AwaitingAgentConfirmation), preflight.Readiness);
        Assert.Equal(nameof(TargetReadiness.AwaitingAgentConfirmation), created.Target.Readiness);
    }

    [Fact]
    public async Task ServiceDiscoveryIsLocalOnly()
    {
        var machine = _h.SeedMachine();
        _h.WindowsServices.Overrides["OrdersService"] = FakeWindowsServices.Eligible("OrdersService");
        _h.WindowsServices.Overrides["Spooler"] = FakeWindowsServices.Eligible("Spooler") with { Eligibility = ServiceEligibility.WindowsComponent };

        var services = await Service().ListWindowsServicesAsync(machine.Id, default);
        Assert.Contains(services!, s => s.ServiceName == "OrdersService" && s.Eligible);
        Assert.Contains(services!, s => s.ServiceName == "Spooler" && !s.Eligible && s.EligibilityDetail is not null);

        _h.WindowsServices.LocalHost = _ => false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ListWindowsServicesAsync(machine.Id, default));
        Assert.Null(await Service().ListWindowsServicesAsync(Guid.NewGuid(), default));
    }

    // --- Real Windows inspector: read-only facts only ---

    [Fact]
    public void RealInspectorReportsANonexistentServiceWithoutSideEffects()
    {
        if (!OperatingSystem.IsWindows()) return;
        var probe = WindowsServiceInspector.Instance.Probe("KaironDefinitelyMissing" + Guid.NewGuid().ToString("N")[..8]);
        Assert.False(probe.Exists);
        Assert.Equal(1060, probe.Win32Error);
    }
}
