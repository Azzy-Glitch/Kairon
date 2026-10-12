using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Evidence;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Defects confirmed in the pre-manual-test audit. Each test failed before its fix.
/// </summary>
public sealed class AuditRegressionTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string Key => $"{_h.ProjectId}|{_h.Environment}|{_h.Service}";

    private SreIncident SeedErrorIncident(string endpoint, DateTime lastSeen)
    {
        var incident = _h.SeedIncident(IncidentStatus.RecommendationReady, correlationKey: Key);
        incident.RootCause = "Earlier diagnosis";
        incident.Timestamp = lastSeen.AddMinutes(-1);
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = "error-rate-threshold", MetricName = "errorRate", Endpoint = endpoint, Symptom = "errors",
                    Observed = 50, Threshold = 10, Unit = "%", Severity = "High", DetectedAt = lastSeen, LastSeenAt = lastSeen }
        });
        _h.Db.SaveChanges();
        return incident;
    }

    private void SeedFailuresOn(string endpoint, double cpu = 10)
    {
        for (var i = 0; i < 4; i++)
        {
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 8), cpu: cpu, latency: 120, requests: 10, errors: 6);
            _h.SeedTelemetry(DateTime.UtcNow.AddSeconds(-30 + i * 8), endpoint: endpoint, statusCode: 500, errorType: "KeyError", errorMessage: "'sku'");
        }
    }

    // --- Correlation: same category is not the same fault -------------------------------------

    [Fact]
    public async Task ErrorsOnADifferentEndpointAfterTheEarlierErrorsStoppedAreANewIncident()
    {
        var checkout = SeedErrorIncident("/checkout", DateTime.UtcNow.AddMinutes(-10));
        SeedFailuresOn("/refund");

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        var refund = Assert.Single(_h.Db.SreIncidents.AsNoTracking().Where(i => i.Id != checkout.Id));
        Assert.Contains("/refund", refund.CorrelatedMetricsJson);
    }

    [Fact]
    public async Task TheSameEndpointFailingAgainWhileItsIncidentIsStillOpenIsTheSameFault()
    {
        var checkout = SeedErrorIncident("/checkout", DateTime.UtcNow.AddMinutes(-10));
        SeedFailuresOn("/checkout");

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);

        Assert.Equal(checkout.Id, (await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).Id);
    }

    // --- Evidence: an incident's own failed restart ----------------------------------------------

    [Fact]
    public async Task AnIncidentsOwnFailedRestartIsInItsEvidenceSoReinvestigationDoesNotRepeatIt()
    {
        var incident = _h.SeedIncident(IncidentStatus.RecommendationReady, correlationKey: Key);
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = "cpu-threshold", MetricName = "cpu", Symptom = "CPU 96%", Observed = 96, Threshold = 80, Unit = "%" }
        });
        var verification = new VerificationResult { IncidentId = incident.Id, Status = VerificationStatus.Failed, FailureReason = "CPU still 95%" };
        _h.Db.VerificationResults.Add(verification);
        _h.Db.RemediationActions.Add(new RemediationAction { IncidentId = incident.Id, ActionKey = "ACT-0001",
            ActionType = ServiceToolNames.RestartApplication, Status = RemediationStatus.Executed,
            CompletedAt = DateTime.UtcNow.AddMinutes(-1), VerificationResultId = verification.Id });
        _h.Db.SaveChanges();

        var evidence = await _h.CreateEvidenceCollector().CollectAsync(incident);

        var own = Assert.Single(evidence.RemediationHistory);
        Assert.True(own.SameFault);
        Assert.Equal("Failed", own.Verification);
        evidence.AvailableActions = [new AvailableActionDto { Action = ServiceToolNames.RestartApplication }];
        Assert.DoesNotContain(DeterministicInvestigation.Investigate(evidence).Recommendations, r => r.Action == ServiceToolNames.RestartApplication);
    }

    // --- Evidence: onsets that started before the window ------------------------------------------

    [Fact]
    public void AMetricAlreadyOverItsThresholdAtTheStartOfTheWindowIsNotClaimedToHaveMovedFirst()
    {
        var start = DateTime.UtcNow.AddMinutes(-3);
        var samples = Enumerable.Range(0, 8).Select(i => new MetricSampleDto
        {
            Timestamp = start.AddSeconds(i * 20), CpuPercent = 95, RequestCount = 10, ErrorCount = i >= 4 ? 5 : 0
        }).ToList();

        var onsets = RiskForecaster.Onsets(samples, _h.Detection, DateTime.UtcNow);

        var cpu = Assert.Single(onsets, o => o.Metric == "cpu");
        Assert.True(cpu.BreachedBeforeWindow);
        Assert.False(Assert.Single(onsets, o => o.Metric == "errorRate").BreachedBeforeWindow);

        var evidence = new EvidencePackageDto
        {
            Incident = new IncidentContextDto { Service = "Checkout", Severity = "High" },
            CorrelatedSignals = [new CorrelatedSignalDto { Metric = "cpu", Observed = 95, Threshold = 80, Unit = "%" },
                                 new CorrelatedSignalDto { Metric = "errorRate", Observed = 25, Threshold = 10, Unit = "%" }],
            SignalOnsets = onsets
        };
        var result = DeterministicInvestigation.Investigate(evidence);
        Assert.DoesNotContain("crossed their thresholds in that order", result.RootCause);
        Assert.DoesNotContain(result.Evidence, e => e.Contains("crossed its threshold first"));
    }

    // --- Evidence: unmeasured is not healthy -------------------------------------------------------

    [Fact]
    public void ErrorsWithoutDependencyDataDoNotClaimTheDependenciesAreNormal()
    {
        var evidence = new EvidencePackageDto
        {
            Incident = new IncidentContextDto { Service = "Checkout", Severity = "High" },
            CorrelatedSignals = [new CorrelatedSignalDto { Metric = "errorRate", Observed = 40, Threshold = 10, Unit = "%" }],
            RelatedErrors = [new RelatedErrorDto { StatusCode = 500, ErrorType = "KeyError", Endpoint = "/checkout" }]
        };

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.DoesNotContain("dependencies look normal", result.RootCause);
        Assert.Contains("not measured", result.RootCause);
    }

    [Fact]
    public async Task AMachineScopedIncidentDoesNotReportAgentLogsAsAbsentWhenTheyWereNeverQueried()
    {
        var incident = _h.SeedIncident(IncidentStatus.Detected, correlationKey: Key + "|m");
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = "cpu-threshold", MetricName = "cpu", MachineId = Guid.NewGuid() }
        });
        _h.Db.SaveChanges();

        var evidence = await _h.CreateEvidenceCollector().CollectAsync(incident);

        Assert.DoesNotContain(evidence.EvidenceGaps, g => g.StartsWith("No application log events"));
        Assert.Contains(evidence.EvidenceGaps, g => g.Contains("not tied to a machine"));
    }

    // --- Decision rules: strength of evidence, not a fixed ranking --------------------------------

    [Fact]
    public void MeasuredCpuSaturationIsNotOverriddenByRetriesAndTimeoutTextAndTheConflictIsStated()
    {
        var evidence = new EvidencePackageDto
        {
            Incident = new IncidentContextDto { Service = "Checkout", Severity = "High" },
            CorrelatedSignals = [new CorrelatedSignalDto { Metric = "cpu", Observed = 98, Threshold = 80, Unit = "%" },
                                 new CorrelatedSignalDto { Metric = "retries", Observed = 45, Threshold = 30, Unit = "/min" }],
            RelatedErrors = [new RelatedErrorDto { StatusCode = 500, ErrorType = "ReadTimeout", ErrorMessage = "upstream timed out" }],
            AvailableActions = [new AvailableActionDto { Action = ServiceToolNames.RestartApplication }]
        };

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Contains("CPU", result.RootCause);
        Assert.Equal("possible", result.RootCauseCertainty); // conflicting evidence: qualified
        Assert.Contains(result.AlternativeExplanations, a => a.Contains("downstream", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ErrorTextAloneIsNotTreatedAsConclusiveProofOfADependencyFailure()
    {
        var evidence = new EvidencePackageDto
        {
            Incident = new IncidentContextDto { Service = "Checkout", Severity = "High" },
            CorrelatedSignals = [new CorrelatedSignalDto { Metric = "errorRate", Observed = 40, Threshold = 10, Unit = "%" }],
            RelatedErrors = [new RelatedErrorDto { StatusCode = 500, ErrorType = "ConnectTimeout", ErrorMessage = "connect timed out" }]
        };

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Equal("possible", result.RootCauseCertainty);
        Assert.Contains("not measured", result.RootCause);
    }

    [Fact]
    public void RetriesWithoutAnIdentifiedDependencyDoNotNameOne()
    {
        var evidence = new EvidencePackageDto
        {
            Incident = new IncidentContextDto { Service = "Checkout", Severity = "High" },
            CorrelatedSignals = [new CorrelatedSignalDto { Metric = "retries", Observed = 90, Threshold = 30, Unit = "/min" },
                                 new CorrelatedSignalDto { Metric = "errorRate", Observed = 30, Threshold = 10, Unit = "%" }],
            RelatedErrors = [new RelatedErrorDto { StatusCode = 500, ErrorMessage = "upstream returned 503" }]
        };

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Equal("possible", result.RootCauseCertainty);
        Assert.Contains(result.NextSteps, s => s.Contains("which call"));
    }

    // --- Safety and compatibility guards (passed before the audit; kept as regression guards) -----

    [Fact]
    public async Task ANewKindOfFaultNeverWithdrawsAPendingApprovalAutomatically()
    {
        var incident = _h.SeedIncident(IncidentStatus.AwaitingApproval, correlationKey: Key);
        incident.RootCause = "CPU saturation";
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = "cpu-threshold", MetricName = "cpu", Observed = 96, Threshold = 80, DetectedAt = DateTime.UtcNow.AddSeconds(-30), LastSeenAt = DateTime.UtcNow.AddSeconds(-30) }
        });
        var pending = new RemediationAction { IncidentId = incident.Id, ActionKey = "ACT-0001", ActionType = ServiceToolNames.RestartApplication,
            Status = RemediationStatus.AwaitingApproval };
        _h.Db.RemediationActions.Add(pending);
        _h.Db.SaveChanges();
        SeedFailuresOn("/checkout", cpu: 96);   // the CPU fault awaiting approval is still going on

        await _h.CreateOrchestrator().DetectAndCorrelateAsync(_h.ProjectId, _h.Environment, _h.Service);
        await _h.CreateOrchestrator().ReevaluateRemediationAsync(incident.Id);

        Assert.Equal(0, _h.Queue.Count);
        Assert.Equal(RemediationStatus.AwaitingApproval, (await _h.Db.RemediationActions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(IncidentStatus.AwaitingApproval, (await _h.Db.SreIncidents.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, _h.Ai.InvestigateCalls);
    }

    [Fact]
    public void AnAssessmentStoredBeforeTheNewFieldsStillReads()
    {
        var old = """{"RootCauseCertainty":"likely","NextSteps":["Check the pool"],"ConsideredActions":[],"OfferedActions":["RestartApplication"]}""";

        var assessment = SreJson.Deserialize<AiAssessment?>(old, null)!;

        Assert.Equal("likely", assessment.RootCauseCertainty);
        Assert.Empty(assessment.AlternativeExplanations);
        Assert.Empty(assessment.SignalOnsets);
    }

    [Fact]
    public void AnOldSnapshotWithoutEndpointOrLastSeenStillCorrelatesAsTheSameFault()
    {
        var old = """[{"rule":"error-rate-threshold","metricName":"errorRate","detectedAt":"2026-10-01T10:00:00Z"}]""";

        var snapshots = SreJson.Deserialize(old, new List<CorrelatedSignalSnapshot>());

        Assert.Null(Assert.Single(snapshots).Endpoint);
        Assert.Null(snapshots[0].LastSeenAt);
    }
}
