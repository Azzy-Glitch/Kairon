using System.Text.RegularExpressions;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Services.Remediation.Tools;

namespace Kairon.Backend.Services;

/// <summary>
/// The deterministic investigation used when AiService:MockMode is on (offline and test runs). It
/// makes the same kind of evidence-based decision a real model is instructed to make - and the
/// Python AI service's mock provider applies the same rules - so test mode exercises the real
/// incident -> recommendation -> policy -> approval flow rather than a canned answer:
///
/// - a dependency failure (connection/timeout/database errors) is not fixed by restarting the app;
/// - a problem that came back soon after a restart is a persistent defect, not a transient fault;
/// - a slowdown with no CPU pressure and no errors points at a slow dependency;
/// - otherwise a sudden fault in a previously healthy process can be mitigated by a restart, which
///   is recommended as a temporary mitigation, never claimed as the fix.
///
/// It only ever recommends actions KAIRON actually offered.
/// </summary>
public static class DeterministicInvestigation
{
    private static readonly Regex DependencyPattern = new(
        @"connection|connect\b|timed? ?out|timeout|refused|unreachable|database|\bdb\b|operationalerror|sql|dns|socket|econn|reset by peer|service unavailable|\b503\b|\b504\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static InvestigationResultDto Investigate(EvidencePackageDto evidence)
    {
        var signals = evidence.CorrelatedSignals;
        var metrics = signals.Select(s => s.Metric).ToHashSet(StringComparer.Ordinal);
        var hasErrors = metrics.Contains("errorRate") || metrics.Contains("errors");
        var hasCpu = metrics.Contains("cpu");
        var hasMemory = metrics.Contains("memory");
        var hasLatency = metrics.Contains("latency");
        var hasRetries = metrics.Contains("retries");

        var errorTexts = evidence.RelatedErrors
            .Select(e => $"{e.ErrorType} {e.ErrorMessage} {(e.StatusCode is 503 or 504 ? e.StatusCode.ToString() : string.Empty)}")
            .Concat(evidence.EndpointBreakdown.Select(e => e.TopErrorType ?? string.Empty));
        var hasCrash = metrics.Contains("processCrash");
        var hasLogPattern = metrics.Contains("logPattern");
        // Dependency error text only counts when the incident itself is about errors or slowness.
        var dependency = (hasErrors || hasLatency || hasRetries) && errorTexts.Any(t => DependencyPattern.IsMatch(t));
        var recurrence = evidence.Recurrence;
        var recurred = recurrence?.RecurredAfterRestart == true;
        var topErrorType = evidence.EndpointBreakdown.Where(e => e.ServerErrors > 0)
            .OrderByDescending(e => e.ServerErrors).Select(e => e.TopErrorType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        var offered = evidence.AvailableActions.Select(a => a.Action).ToList();
        var restart = offered.FirstOrDefault(a => a == ServiceToolNames.RestartApplication)
                      ?? offered.FirstOrDefault(a => a == ServiceToolNames.RestartService);
        var healthCheck = offered.FirstOrDefault(a => a == ServiceToolNames.RunHealthCheck);
        var start = offered.FirstOrDefault(a => a == ServiceToolNames.StartService);

        string rootCause, certainty, restartVerdictReason;
        double confidence;
        var restartFits = false;
        var startFits = false;
        var nextSteps = new List<string>();

        if (signals.Count == 0)
        {
            rootCause = "The evidence is not sufficient to identify a cause.";
            (certainty, confidence) = ("unknown", 0.3);
            restartVerdictReason = "There is not enough evidence to justify changing anything yet.";
            nextSteps.Add("Wait for more telemetry, then re-investigate.");
        }
        else if (hasCrash)
        {
            rootCause = "The monitored process stopped running unexpectedly.";
            (certainty, confidence) = ("confirmed", 0.95);
            restartVerdictReason = "The process is not running, so there is nothing to restart; it needs starting.";
            startFits = true;
            nextSteps.Add("Check the application's logs for why the process exited.");
        }
        else if (recurred)
        {
            rootCause = $"The problem returned {recurrence!.MinutesSinceRestart:0} minute(s) after a restart, so it is not a one-off transient fault; the underlying cause is still present.";
            (certainty, confidence) = ("possible", 0.6);
            restartVerdictReason = "A restart already cleared this once and the problem came back; another restart would only hide it briefly.";
            nextSteps.Add("Investigate the application: recent deployments or configuration changes, and the exceptions in the evidence.");
            if (topErrorType is not null) nextSteps.Add($"Start with the most frequent exception, {topErrorType}, and the code path that raises it.");
        }
        else if (hasRetries)
        {
            rootCause = "Repeated retries may be amplifying a downstream failure; the cause is not yet confirmed.";
            (certainty, confidence) = ("possible", dependency ? 0.92 : 0.6);
            restartVerdictReason = "Retries point at a failing downstream call; restarting the app would reset the retry loop only until it builds up again.";
            nextSteps.Add("Find the downstream call being retried and check that dependency.");
            nextSteps.Add("Add retry limits with backoff so a failing dependency cannot cause a retry storm.");
        }
        else if (hasCpu || hasMemory)
        {
            rootCause = hasCpu && hasLatency ? "CPU saturation in the app's own process is driving request latency up."
                : hasCpu ? "The app's own process is saturating the CPU."
                : "The app's own process is using an abnormal amount of memory.";
            (certainty, confidence) = ("likely", 0.78);
            restartFits = true;
            restartVerdictReason = "The pressure is inside the app's own process, which was healthy before; a restart clears the runaway work or memory and should restore service as a temporary mitigation. It will build up again if the cause is in the code.";
            nextSteps.Add("Find the CPU- or memory-heavy work (background jobs, hot loops, caches) so it does not build up again.");
        }
        else if (dependency)
        {
            rootCause = "Requests are failing or slowing while calling a dependency (connection, timeout or database errors in the evidence).";
            (certainty, confidence) = ("likely", 0.72);
            restartVerdictReason = "The errors point at a dependency the app calls; restarting the app would not bring that dependency back.";
            nextSteps.Add("Check the dependency named in the errors (database, downstream API, network) and restore it.");
            nextSteps.Add("Consider timeouts and a circuit breaker so the app fails fast while the dependency is down.");
        }
        else if (hasLatency && !hasErrors)
        {
            rootCause = "Requests are slow without CPU pressure or errors, which usually means waiting on something slow (a dependency or lock).";
            (certainty, confidence) = ("possible", 0.55);
            restartVerdictReason = "Nothing in the evidence suggests the slowdown is inside the app's own process, so a restart is unlikely to help.";
            nextSteps.Add("Check the latency of the dependencies the slow endpoint calls.");
        }
        else
        {
            restartFits = true;
            if (hasLogPattern && !hasErrors)
            {
                rootCause = "Application logs show a repeated error pattern, most likely an unhandled exception; the evidence does not show its cause.";
                (certainty, confidence) = ("possible", 0.68);
            }
            else
            {
                rootCause = topErrorType is null
                    ? "The app started returning server errors; the evidence does not show why."
                    : $"The app started returning server errors ({topErrorType}); the evidence does not show whether this is bad in-process state or a code defect.";
                (certainty, confidence) = ("possible", 0.6);
            }
            restartVerdictReason = "The fault began in a previously healthy process with no sign of a failing dependency; a restart clears in-process state and should restore service as a temporary mitigation. It cannot fix a code defect - if the problem returns after the restart, the code needs investigating.";
            nextSteps.Add("After the restart, watch whether the problem returns; if it does, investigate the application code.");
            if (topErrorType is not null) nextSteps.Add($"Review the code path that raises {topErrorType}.");
        }

        var considered = new List<ConsideredActionDto>();
        var recommendations = new List<AiRecommendationDto>();
        if (start is not null && startFits)
        {
            considered.Add(new() { Action = start, Verdict = "recommended", Reason = "The process has stopped; starting it restores the service." });
            recommendations.Add(new AiRecommendationDto
            {
                Action = start,
                Reason = "The monitored process stopped running; starting it restores availability.",
                ExpectedOutcome = "The service runs again; KAIRON verifies recovery afterwards.",
                RiskLevel = "medium"
            });
        }
        if (restart is not null)
        {
            considered.Add(new() { Action = restart, Verdict = restartFits ? "recommended" : "not_recommended", Reason = restartVerdictReason });
            if (restartFits)
            {
                recommendations.Add(new AiRecommendationDto
                {
                    Action = restart,
                    Reason = restartVerdictReason,
                    ExpectedOutcome = "The process starts fresh and the symptoms clear, if they were caused by in-process state. KAIRON verifies recovery afterwards.",
                    RiskLevel = "medium"
                });
            }
        }
        if (healthCheck is not null && recommendations.Count == 0 && signals.Count > 0)
        {
            considered.Add(new() { Action = healthCheck, Verdict = "recommended", Reason = "A read-only check of the target's state, useful while the cause is investigated." });
            recommendations.Add(new AiRecommendationDto
            {
                Action = healthCheck,
                Reason = "Confirm the target's current state while the cause is investigated.",
                ExpectedOutcome = "Current service state; this read-only check does not repair anything.",
                RiskLevel = "low"
            });
        }

        var forecast = evidence.RiskForecast;
        var hasForecast = forecast is not null && forecast.Outcome != RiskForecastOutcomes.Inconclusive;

        return new InvestigationResultDto
        {
            Summary = $"{evidence.Incident.Service} is degraded: {string.Join("; ", evidence.Incident.Symptoms.Take(3))}",
            RootCause = rootCause,
            RootCauseCertainty = certainty,
            ContributingFactors = signals.Select(s => s.Symptom).Take(5).ToList(),
            Evidence = signals.Select(s => $"{s.Metric} {s.Observed}{s.Unit} vs threshold {s.Threshold}{s.Unit}").Take(6).ToList(),
            Confidence = confidence,
            Severity = evidence.Incident.Severity.ToLowerInvariant(),
            AffectedComponents = new List<string> { evidence.Incident.AffectedComponent, evidence.Incident.Service }
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList(),
            PredictedFailure = hasForecast ? forecast!.FailureMode : "Prediction inconclusive: not enough trend data.",
            EstimatedRisk = hasForecast ? forecast!.RiskLevel : "medium",
            Recommendations = recommendations,
            NextSteps = nextSteps,
            ConsideredActions = considered,
            Provider = "mock",
            Model = "deterministic-mock"
        };
    }
}
