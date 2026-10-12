using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Evidence;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// KAIRON's deterministic trend forecast (RiskForecaster) and the deterministic investigation used
/// in offline/test mode (DeterministicInvestigation), which applies the same decision rules the AI
/// is instructed to follow.
/// </summary>
public sealed class RiskForecastAndInvestigationTests : IDisposable
{
    private static readonly DetectionOptions Thresholds = new();
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static List<MetricSampleDto> Samples(params (long requests, long errors, double? latency, double? cpu)[] points)
    {
        var start = DateTime.UtcNow.AddMinutes(-4);
        return points.Select((p, i) => new MetricSampleDto
        {
            Timestamp = start.AddSeconds(i * 15),
            RequestCount = p.requests,
            ErrorCount = p.errors,
            ResponseTimeMs = p.latency,
            CpuPercent = p.cpu
        }).ToList();
    }

    // --- Forecast ---------------------------------------------------------------------------

    [Fact]
    public void ARisingErrorRateProducesAnEvidenceBasedElevatedForecast()
    {
        var forecast = RiskForecaster.Forecast(
            Samples((10, 0, 100, 10), (10, 0, 100, 10), (10, 1, 100, 10), (10, 5, 100, 10), (10, 6, 100, 10), (10, 7, 100, 10)),
            recurrence: null, Thresholds);

        Assert.Equal(RiskForecastOutcomes.Elevated, forecast.Outcome);
        Assert.Contains(forecast.RiskLevel, new[] { "high", "critical" });
        var errors = Assert.Single(forecast.Trends, t => t.Metric == "errorRate");
        Assert.Equal("rising", errors.Direction);
        Assert.Equal(1.0, errors.Persistence);
        Assert.Contains(forecast.Evidence, e => e.StartsWith("Error rate rising"));
        Assert.Contains("server errors", forecast.FailureMode);
        Assert.True(forecast.HorizonMinutes > 0);
        Assert.InRange(forecast.Confidence, 0.01, 0.9);
    }

    [Fact]
    public void TooLittleTelemetryIsExplicitlyInconclusive()
    {
        var forecast = RiskForecaster.Forecast(Samples((10, 5, 100, 10), (10, 6, 100, 10)), recurrence: null, Thresholds);

        Assert.Equal(RiskForecastOutcomes.Inconclusive, forecast.Outcome);
        Assert.Equal("unknown", forecast.RiskLevel);
        Assert.Equal(0, forecast.Confidence);
        Assert.Contains(forecast.Evidence, e => e.Contains("not enough to project a trend"));
    }

    [Fact]
    public void HighLatencyAndCpuPressureProduceARelevantForecast()
    {
        var forecast = RiskForecaster.Forecast(
            Samples((10, 0, 300, 40), (10, 0, 400, 50), (10, 0, 1600, 95), (10, 0, 1900, 97), (10, 0, 2100, 98), (10, 0, 2300, 99)),
            recurrence: null, Thresholds);

        Assert.Equal(RiskForecastOutcomes.Elevated, forecast.Outcome);
        Assert.Equal("critical", forecast.RiskLevel); // two metrics breaching and rising
        Assert.Contains(forecast.Trends, t => t.Metric == "latency" && t.Direction == "rising");
        Assert.Contains(forecast.Trends, t => t.Metric == "cpu" && t.Direction == "rising");
    }

    [Fact]
    public void AHealthyAppForecastsStableLowRisk()
    {
        var forecast = RiskForecaster.Forecast(
            Samples((10, 0, 120, 12), (10, 0, 110, 11), (10, 0, 130, 13), (10, 0, 120, 12)), recurrence: null, Thresholds);

        Assert.Equal(RiskForecastOutcomes.Stable, forecast.Outcome);
        Assert.Equal("low", forecast.RiskLevel);
    }

    [Fact]
    public void AProblemThatReturnedAfterARestartRaisesTheRiskAndSaysSo()
    {
        var samples = Samples((10, 2, 100, 10), (10, 2, 100, 10), (10, 2, 100, 10), (10, 2, 100, 10));
        var plain = RiskForecaster.Forecast(samples, recurrence: null, Thresholds);
        var recurred = RiskForecaster.Forecast(samples, new RecurrenceContextDto
        {
            IncidentsLast24h = 1, RecurredAfterRestart = true, MinutesSinceRestart = 4, PreviousRemediation = "RestartApplication"
        }, Thresholds);

        Assert.True(Rank(recurred.RiskLevel) > Rank(plain.RiskLevel));
        Assert.Contains("returned after a restart", recurred.FailureMode);
        Assert.Contains(recurred.Evidence, e => e.Contains("RestartApplication 4 minute(s) ago"));
        Assert.Contains("Investigate the application", recurred.PreventiveAction);
    }

    private static int Rank(string level) => Array.IndexOf(new[] { "low", "medium", "high", "critical" }, level);

    // --- Deterministic investigation (offline/test mode) ---------------------------------------

    private static EvidencePackageDto Evidence(string[] metrics, string[] offered, string? errorType = null,
        string? errorMessage = null, bool recurred = false)
    {
        var evidence = new EvidencePackageDto
        {
            Incident = new IncidentContextDto { Service = "Checkout", Severity = "High", Symptoms = new() { "Error rate 60%" } },
            CorrelatedSignals = metrics.Select(m => new CorrelatedSignalDto { Metric = m, Symptom = m, Observed = 60, Threshold = 10, Unit = "%" }).ToList(),
            AvailableActions = offered.Select(a => new AvailableActionDto { Action = a, RiskLevel = "Medium" }).ToList(),
            Recurrence = new RecurrenceContextDto { RecurredAfterRestart = recurred, MinutesSinceRestart = recurred ? 3 : null }
        };
        if (errorType is not null || errorMessage is not null)
        {
            evidence.RelatedErrors.Add(new RelatedErrorDto { StatusCode = 500, ErrorType = errorType, ErrorMessage = errorMessage, Endpoint = "/checkout" });
            evidence.EndpointBreakdown.Add(new EndpointSummaryDto { Endpoint = "/checkout", Requests = 10, ServerErrors = 6, TopErrorType = errorType });
        }
        return evidence;
    }

    [Fact]
    public void ServerErrorsWithNormalResourcesAreInvestigatedNotRestarted()
    {
        var evidence = Evidence(["errorRate"], [ServiceToolNames.RestartApplication], errorType: "KeyError", errorMessage: "'sku'");
        evidence.RelatedErrors[0].StackExcerpt = "Traceback (most recent call last):\n  File \"app.py\", line 42, in checkout\nKeyError: 'sku'";

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Empty(result.Recommendations);
        var restart = Assert.Single(result.ConsideredActions);
        Assert.Equal("not_recommended", restart.Verdict);
        Assert.Contains("code path", restart.Reason);
        Assert.Contains("KeyError", result.RootCause);
        // Unmeasured is not healthy: the evidence has no dependency data, so it must not be called normal.
        Assert.Contains("dependency calls are not measured", result.RootCause);
        Assert.Contains(result.NextSteps, s => s.Contains("KeyError") && s.Contains("stack trace"));
        Assert.NotEmpty(result.AlternativeExplanations);
        Assert.NotEmpty(result.VerificationSteps);
        Assert.NotEqual("confirmed", result.RootCauseCertainty);
    }

    [Fact]
    public void ErrorsFromTheProcesssOwnStateGetARestartAsATemporaryMitigation()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(
            ["errorRate"], [ServiceToolNames.RestartApplication], errorType: "OSError", errorMessage: "[Errno 24] Too many open files"));

        var rec = Assert.Single(result.Recommendations);
        Assert.Equal(ServiceToolNames.RestartApplication, rec.Action);
        Assert.Equal("temporary_mitigation", rec.Kind);
        Assert.Contains("temporary mitigation", rec.Reason);
        Assert.NotEmpty(rec.Risks);
        Assert.NotEmpty(rec.Verification);
    }

    [Fact]
    public void AFailingUpstreamApiWithRetriesIsDiagnosedAsTheUpstreamNotTheApp()
    {
        var evidence = Evidence(["errorRate", "retries"], [ServiceToolNames.RestartApplication],
            errorType: "HTTPException", errorMessage: "Payment provider rejected the charge");
        evidence.Dependencies.Add(new DependencySummaryDto { Target = "payments.example:443/charge", Calls = 90, Failures = 63, AvgDurationMs = 120 });

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Empty(result.Recommendations);
        Assert.Contains("payments.example:443/charge", result.RootCause);
        Assert.Contains("retrying", result.RootCause);
        Assert.Contains(result.NextSteps, s => s.Contains("rate limiting"));
        Assert.Contains(result.NextSteps, s => s.Contains("circuit breaker"));
    }

    [Fact]
    public void DatabaseLatencyBeforeApiErrorsPointsAtTheDatabase()
    {
        var evidence = Evidence(["errorRate", "latency"], [ServiceToolNames.RestartApplication]);
        evidence.Dependencies.Add(new DependencySummaryDto { Target = "orders-db:5432", Calls = 200, Failures = 12, AvgDurationMs = 2400 });
        evidence.SignalOnsets.Add(new SignalOnsetDto { Metric = "latency", Label = "Latency", SecondsBeforeDetection = 90, FirstBreachAt = DateTime.UtcNow.AddSeconds(-90) });
        evidence.SignalOnsets.Add(new SignalOnsetDto { Metric = "errorRate", Label = "Error rate", SecondsBeforeDetection = 20, FirstBreachAt = DateTime.UtcNow.AddSeconds(-20) });

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Empty(result.Recommendations);
        Assert.Contains("orders-db:5432", result.RootCause);
        Assert.Contains("latency rose before the errors", result.RootCause);
        Assert.Contains(result.NextSteps, s => s.Contains("slow-query") && s.Contains("connection-pool"));
        Assert.Contains(result.NextSteps, s => s.Contains("Only add database capacity if"));
        Assert.Contains(result.ConsideredActions, c => c.Verdict == "not_recommended" && c.Reason.Contains("database"));
    }

    [Fact]
    public void QueueAndRetriesRisingTogetherPointAtWorkerCapacityAndRetryAmplification()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(["queue", "retries"], [ServiceToolNames.RestartApplication]));

        Assert.Empty(result.Recommendations);
        Assert.Contains(result.NextSteps, s => s.Contains("worker capacity"));
        Assert.Contains(result.NextSteps, s => s.Contains("retry amplification"));
    }

    [Fact]
    public void GrowingMemoryIsExplainedAsMemoryPressureWithARestartOnlyBuyingTime()
    {
        var evidence = Evidence(["memory"], [ServiceToolNames.RestartApplication]);
        evidence.RiskForecast = new RiskForecastDto { Outcome = RiskForecastOutcomes.Elevated, RiskLevel = "high",
            Trends = [new MetricTrendDto { Metric = "memory", Direction = "rising", Earlier = 60, Recent = 88, Threshold = 85 }] };

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Contains("growing steadily", result.RootCause);
        Assert.Contains("buys time", Assert.Single(result.Recommendations).Reason);
        Assert.Contains(result.NextSteps, s => s.Contains("leak"));
    }

    [Fact]
    public void MissingEvidenceIsReportedRatherThanGuessedAt()
    {
        var evidence = Evidence(["errorRate"], []);
        evidence.EvidenceGaps.Add("Server errors without stack traces - the failing code location is not visible.");

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Equal("unknown", result.RootCauseCertainty);   // no exception detail at all
        Assert.Contains(result.MissingEvidence, m => m.Contains("stack traces"));
        Assert.Contains(result.NextSteps, s => s.Contains("carry no exception detail"));
    }

    [Fact]
    public void ARestartThatFailedForTheSameFaultIsNotRecommendedAgain()
    {
        var evidence = Evidence(["cpu"], [ServiceToolNames.RestartApplication]);
        evidence.RemediationHistory.Add(new RemediationOutcomeDto { IncidentKey = "INC-0003", Action = ServiceToolNames.RestartApplication,
            Status = "Executed", Verification = "Failed", SameFault = true });

        var result = DeterministicInvestigation.Investigate(evidence);

        Assert.Empty(result.Recommendations);
        Assert.Contains("INC-0003", result.RootCause);
        Assert.Contains("verification Failed", result.RootCause);
    }

    [Fact]
    public void ErrorsThatReturnedAfterARestartAreSentForInvestigationNotRestartedAgain()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(
            ["errorRate"], [ServiceToolNames.RestartApplication], errorType: "KeyError", recurred: true));

        Assert.Empty(result.Recommendations);
        Assert.Contains(result.ConsideredActions, c => c.Action == ServiceToolNames.RestartApplication && c.Verdict == "not_recommended");
        Assert.Contains(result.NextSteps, s => s.Contains("KeyError"));
        Assert.NotEqual("confirmed", result.RootCauseCertainty);
    }

    [Fact]
    public void CpuPressureInTheAppsOwnProcessGetsARestartMitigation()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(["cpu", "latency"], [ServiceToolNames.RestartApplication]));

        Assert.Equal(ServiceToolNames.RestartApplication, Assert.Single(result.Recommendations).Action);
        Assert.Equal("likely", result.RootCauseCertainty);
    }

    [Fact]
    public void SlownessWithoutCpuPressureOrErrorsPointsAtADependency()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(["latency"], [ServiceToolNames.RestartApplication]));

        Assert.Empty(result.Recommendations);
        Assert.Contains(result.NextSteps, s => s.Contains("downstream APIs"));
    }

    [Fact]
    public void NothingIsRecommendedThatKaironDidNotOffer()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(["errorRate"], offered: []));

        Assert.Empty(result.Recommendations);
        Assert.Empty(result.ConsideredActions);
        Assert.NotEmpty(result.RootCause);
    }

    [Fact]
    public void NoSignalsMeansAnInconclusiveInvestigation()
    {
        var result = DeterministicInvestigation.Investigate(Evidence([], [ServiceToolNames.RestartApplication]));

        Assert.Empty(result.Recommendations);
        Assert.Equal("unknown", result.RootCauseCertainty);
        Assert.StartsWith("Prediction inconclusive", result.PredictedFailure);
    }

    // --- Evidence the collector assembles from the database ----------------------------------

    [Fact]
    public async Task EvidenceCarriesStackExcerptsDependenciesOnsetsPastOutcomesAndGaps()
    {
        var now = DateTime.UtcNow;
        // Latency breaches first, errors follow.
        for (var i = 0; i < 6; i++)
            _h.SeedMetric(now.AddSeconds(-100 + i * 15), cpu: 20, latency: i >= 1 ? 1600 : 200, requests: 10, errors: i >= 3 ? 5 : 0);
        var error = _h.SeedTelemetry(now.AddSeconds(-20), statusCode: 500, errorType: "KeyError", errorMessage: "'sku'");
        error.StackTrace = string.Join("\n", Enumerable.Range(1, 20).Select(n => $"  frame {n}")) + "\nKeyError: 'sku'";
        var dependency = _h.SeedTelemetry(now.AddSeconds(-15), endpoint: "orders-db:5432", statusCode: 503, durationMs: 3000, errorType: null, errorMessage: null);
        dependency.Method = "DEPENDENCY";
        // An earlier restart for the same kind of fault whose verification failed.
        var earlier = _h.SeedIncident(IncidentStatus.Failed, correlationKey: "earlier");
        earlier.Timestamp = now.AddMinutes(-30);
        earlier.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot> { new() { Rule = "error-rate-threshold", MetricName = "errorRate" } });
        var verification = new VerificationResult { IncidentId = earlier.Id, Status = VerificationStatus.Failed, RecoveryScore = 0.2, FailureReason = "Error rate still 40%" };
        _h.Db.VerificationResults.Add(verification);
        _h.Db.RemediationActions.Add(new RemediationAction { IncidentId = earlier.Id, ActionKey = "ACT-0009", ActionType = ServiceToolNames.RestartApplication,
            Status = RemediationStatus.Executed, CompletedAt = now.AddMinutes(-28), VerificationResultId = verification.Id });
        var incident = _h.SeedIncident(IncidentStatus.Detected, correlationKey: "current");
        incident.Timestamp = now;
        incident.CorrelatedMetricsJson = SreJson.Serialize(new List<CorrelatedSignalSnapshot>
        {
            new() { Rule = "error-rate-threshold", MetricName = "errorRate", Observed = 50, Threshold = 10, Unit = "%" },
            new() { Rule = "latency-threshold", MetricName = "latency", Observed = 1600, Threshold = 1000, Unit = "ms" }
        });
        _h.Db.SaveChanges();

        var evidence = await _h.CreateEvidenceCollector().CollectAsync(incident);

        var excerpt = Assert.Single(evidence.RelatedErrors, e => e.ErrorType == "KeyError").StackExcerpt!;
        Assert.Contains("frame 1", excerpt);
        Assert.Contains("KeyError: 'sku'", excerpt);
        Assert.DoesNotContain("frame 10", excerpt);   // the middle is elided
        var db = Assert.Single(evidence.Dependencies);
        Assert.Equal("orders-db:5432", db.Target);
        Assert.Equal(1, db.Failures);
        Assert.Equal(new[] { "latency", "errorRate" }, evidence.SignalOnsets.Select(o => o.Metric).ToArray());
        Assert.True(evidence.SignalOnsets[0].SecondsBeforeDetection > evidence.SignalOnsets[1].SecondsBeforeDetection);
        var past = Assert.Single(evidence.RemediationHistory);
        Assert.Equal(("Failed", true), (past.Verification, past.SameFault));
        Assert.Contains("Error rate still 40%", past.Error);
        Assert.Contains(evidence.EvidenceGaps, g => g.Contains("memory"));
        Assert.Contains(evidence.EvidenceGaps, g => g.Contains("database server metrics"));
        Assert.DoesNotContain(evidence.EvidenceGaps, g => g.Contains("per-dependency"));

        // And the investigation uses it: the failed restart for this same fault comes first, so no
        // restart is suggested again.
        var result = DeterministicInvestigation.Investigate(evidence);
        Assert.Contains("did not resolve it", result.RootCause);
        Assert.Contains("verification Failed", result.RootCause);
        Assert.DoesNotContain(result.Recommendations, r => r.Action.StartsWith("Restart"));
    }

    // --- Persisted forecast and assessment --------------------------------------------------

    [Fact]
    public async Task TheForecastIsRecordedEvenWhenTheAiIsUnavailable()
    {
        for (var i = 0; i < 6; i++)
            _h.SeedMetric(DateTime.UtcNow.AddSeconds(-150 + i * 20), cpu: 10, latency: 100, requests: 10, errors: i < 2 ? 0 : 7);
        var incident = _h.SeedIncident();
        _h.Ai.NextException = new AiUnavailableException("AI service is down");

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var updated = await _h.Db.SreIncidents.AsNoTracking().FirstAsync(i => i.Id == incident.Id);
        Assert.Equal(IncidentStatus.Investigating, updated.Status); // still open; detection stands
        var detail = (await _h.CreateQueryService().GetAsync(incident.Id))!;
        Assert.NotNull(detail.Forecast);
        Assert.Equal(RiskForecastOutcomes.Elevated, detail.Forecast!.Outcome);
        Assert.Contains(detail.Forecast.Trends, t => t.Metric == "errorRate" && t.Direction == "rising");
        Assert.Null(detail.Assessment);
    }

    [Fact]
    public async Task TheAssessmentKeepsOnlyOfferedActionsAndExplainsAnEmptyRecommendation()
    {
        var incident = _h.SeedIncident();
        var result = FakeAiService.DefaultResult();
        result.Recommendations = new();
        result.RootCauseCertainty = "Possible";
        result.NextSteps = new() { "Check the payment provider." };
        result.ConsideredActions = new()
        {
            new() { Action = ServiceToolNames.RunHealthCheck, Verdict = "not_recommended", Reason = "It would not change anything." },
            new() { Action = "DeleteDatabase", Verdict = "recommended", Reason = "invented" }
        };
        _h.Ai.NextResult = result;

        await _h.CreateOrchestrator().InvestigateAsync(incident.Id);

        var detail = (await _h.CreateQueryService().GetAsync(incident.Id))!;
        Assert.Equal("possible", detail.Assessment!.RootCauseCertainty);
        Assert.Equal(new[] { "Check the payment provider." }, detail.Assessment.NextSteps);
        var considered = Assert.Single(detail.Assessment.ConsideredActions);
        Assert.Equal(ServiceToolNames.RunHealthCheck, considered.Action);
        Assert.Contains(ServiceToolNames.RunHealthCheck, detail.Assessment.OfferedActions);
        Assert.Equal("The AI considered RunHealthCheck and did not recommend it: It would not change anything.", detail.FailureReason);
    }
}
