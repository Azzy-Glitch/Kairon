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
    public void ASuddenServerErrorSpikeGetsTheOfferedRestartAsAMitigationWithoutClaimingTheCause()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(
            ["errorRate"], [ServiceToolNames.RestartApplication], errorType: "HTTPException", errorMessage: "Payment provider rejected the charge"));

        var rec = Assert.Single(result.Recommendations);
        Assert.Equal(ServiceToolNames.RestartApplication, rec.Action);
        Assert.Contains("temporary mitigation", rec.Reason);
        Assert.Contains("cannot fix a code defect", rec.Reason);
        // An exception alone is not proof of a code bug.
        Assert.Equal("possible", result.RootCauseCertainty);
        Assert.DoesNotContain("confirmed", result.RootCause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.NextSteps, s => s.Contains("HTTPException"));
    }

    [Fact]
    public void ADatabaseOutageGetsADependencyFocusedAnswerNotARestart()
    {
        var result = DeterministicInvestigation.Investigate(Evidence(
            ["errorRate"], [ServiceToolNames.RestartService, ServiceToolNames.RunHealthCheck],
            errorType: "OperationalError", errorMessage: "could not connect to database server: Connection refused"));

        Assert.DoesNotContain(result.Recommendations, r => r.Action == ServiceToolNames.RestartService);
        Assert.Contains(result.ConsideredActions, c => c.Action == ServiceToolNames.RestartService && c.Verdict == "not_recommended"
                                                         && c.Reason.Contains("dependency"));
        Assert.Equal(ServiceToolNames.RunHealthCheck, Assert.Single(result.Recommendations).Action);
        Assert.Contains(result.NextSteps, s => s.Contains("dependency"));
        Assert.Equal("likely", result.RootCauseCertainty);
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
        Assert.Contains(result.NextSteps, s => s.Contains("dependencies"));
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
