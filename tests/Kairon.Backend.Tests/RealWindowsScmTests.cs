using System.Net.Http.Json;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Remediation.Tools;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>Opt-in: runs only when KAIRON_REAL_SCM_TEST_SERVICE names the disposable service created
/// by tools/remediation/New-KaironScmTestService.ps1, with the test runner's account granted
/// Query/Start/Stop on it. Everything else uses the real, unelevated Windows SCM path - the same
/// inspector, sc.exe control and RestartServiceTool production uses. Never targets any other service.</summary>
public sealed class RealScmFactAttribute : FactAttribute
{
    public RealScmFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Real Windows SCM tests require Windows.";
        else if (RealWindowsScmTests.ServiceName is null)
            Skip = "Set KAIRON_REAL_SCM_TEST_SERVICE=ScmTestDependency after running tools/remediation/New-KaironScmTestService.ps1.";
    }
}

public sealed class RealWindowsScmTests
{
    internal static string? ServiceName =>
        Environment.GetEnvironmentVariable("KAIRON_REAL_SCM_TEST_SERVICE") is "ScmTestDependency" ? "ScmTestDependency" : null;

    private static readonly HttpClient Dependency = new() { BaseAddress = new Uri("http://127.0.0.1:18080"), Timeout = TimeSpan.FromSeconds(10) };

    [RealScmFact]
    public void InspectorSeesTheGrantedRightsAndAnEligibleIdentity()
    {
        var probe = WindowsServiceInspector.Instance.Probe(ServiceName!);
        Assert.True(probe.Exists);
        Assert.Equal(ServiceEligibility.Eligible, probe.Eligibility);
        Assert.True(probe.CanQueryStatus && probe.CanStart && probe.CanStop);
        Assert.NotNull(probe.IdentityHash);
        Assert.Contains(WindowsServiceInspector.Instance.ListServices(), s => s.ServiceName == ServiceName && s.Eligibility == ServiceEligibility.Eligible);
    }

    [RealScmFact]
    public async Task RealStopAndStartChangeTheServiceState()
    {
        var control = new WindowsServiceControl();
        var host = Environment.MachineName;
        await EnsureRunningAsync(control);

        await control.ChangeAsync(host, ServiceName!, start: false, default);
        Assert.Equal(1, await WaitForAsync(control, 1));
        await control.ChangeAsync(host, ServiceName!, start: true, default);
        Assert.Equal(4, await WaitForAsync(control, 4));
    }

    [RealScmFact]
    public async Task ApprovedRestartThroughTheProductionToolClearsARealDegradedState()
    {
        var control = new WindowsServiceControl();
        await EnsureRunningAsync(control);
        await WaitForDependencyAsync();
        (await Dependency.PostAsync("/wedge", null)).EnsureSuccessStatusCode();
        Assert.Equal("wedged", (await Dependency.GetFromJsonAsync<Health>("/health"))!.Status);

        using var h = new TestHarness();
        var incident = h.SeedIncident();
        var machine = h.SeedMachine(hostName: Environment.MachineName, agentCredentialHash: "test");
        var credential = h.SeedCredential(incident.ProjectId, "test");
        incident.CorrelatedMetricsJson = SreJson.Serialize(new[] { new CorrelatedSignalSnapshot { MetricName = "errorRate", MachineId = machine.Id } });
        h.Db.SaveChanges();
        var target = h.SeedRemediationTarget(machine.Id, credential.Id, Environment.MachineName, windowsServiceName: ServiceName!,
            allowedOperations: [ServiceToolNames.RestartService]);
        target.ServiceIdentityHash = WindowsServiceInspector.Instance.Probe(ServiceName!).IdentityHash;
        h.Db.SaveChanges();
        var resolver = new RemediationTargetResolver(h.Db, TestHarness.Opt(h.WindowsRemediation), LocalMachine.Instance);
        var tool = new RestartServiceTool(h.Db, resolver, control, WindowsServiceInspector.Instance);

        var fingerprint = await tool.TargetFingerprintAsync(incident);
        Assert.NotNull(fingerprint);
        var result = await tool.ExecuteAsync(new RemediationToolContext
        {
            ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey,
            Environment = incident.Environment, Service = incident.Service, ActionKey = "ACT-real-scm",
            Parameters = new Dictionary<string, string> { ["targetFingerprint"] = fingerprint! }
        });

        Assert.True(result.Success, result.Error);
        Assert.Equal(4, await control.QueryAsync(Environment.MachineName, ServiceName!, default));
        await WaitForDependencyAsync();
        Assert.Equal("ok", (await Dependency.GetFromJsonAsync<Health>("/health"))!.Status);
    }

    [RealScmFact]
    public void AServiceWithoutAKaironGrantIsReportedAsSuchWithoutBeingTouched()
    {
        // Spooler is a Windows component (refused by policy) and grants unelevated users no
        // start/stop; the probe only observes this - it never changes the service.
        var probe = WindowsServiceInspector.Instance.Probe("Spooler");
        if (probe.Exists)
        {
            Assert.NotEqual(ServiceEligibility.Eligible, probe.Eligibility); // type 0x110 / under %SystemRoot%
            Assert.False(probe.CanStop);
        }
        var agent = WindowsServiceInspector.Instance.Probe("Kairon.Agent");
        if (agent.Exists) Assert.Equal(ServiceEligibility.KaironInfrastructure, agent.Eligibility);
    }

    private sealed record Health(string Status);

    private static async Task EnsureRunningAsync(WindowsServiceControl control)
    {
        if (await control.QueryAsync(Environment.MachineName, ServiceName!, default) == 1)
            await control.ChangeAsync(Environment.MachineName, ServiceName!, start: true, default);
        Assert.Equal(4, await WaitForAsync(control, 4));
    }

    private static async Task<int> WaitForAsync(WindowsServiceControl control, int expected)
    {
        var state = 0;
        for (var i = 0; i < 80 && (state = await control.QueryAsync(Environment.MachineName, ServiceName!, default)) != expected; i++)
            await Task.Delay(250);
        return state;
    }

    private static async Task WaitForDependencyAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            try { if ((await Dependency.GetAsync("/health")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
    }
}
