using System.Text.RegularExpressions;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Services.Remediation.Tools;

namespace Kairon.Backend.Services;

/// <summary>
/// The deterministic investigation used when AiService:MockMode is on (offline and test runs). It
/// reads the same evidence a real model gets and applies the reasoning the model is instructed to
/// follow - the Python AI service's mock provider applies the same rules - so test mode exercises
/// the real incident -> recommendation -> policy -> approval flow instead of a canned answer.
///
/// It explains first and acts second: every outcome names what happened, the signals behind it, how
/// certain that is, what else could explain it, what is missing, what to check next and how to verify
/// a fix. An action is recommended only when the evidence says it fits, and a restart is only ever a
/// temporary mitigation. It never recommends an action KAIRON did not offer.
/// </summary>
public static class DeterministicInvestigation
{
    private static readonly Regex DatabasePattern = new(
        @"database|\bsql\b|sqlalchemy|psycopg|operationalerror|mysql|postgres|sqlite|mongo|redis|\bdb\b|query timeout|deadlock|connection pool",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExternalPattern = new(
        @"timed? ?out|timeout|refused|unreachable|dns|socket|econn|reset by peer|service unavailable|bad gateway|gateway|upstream|provider|rate limit|too many requests|\b429\b|\b502\b|\b503\b|\b504\b|connection",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Failures that come from the process's own state - the cases a restart can clear.</summary>
    private static readonly Regex InProcessStatePattern = new(
        @"out ?of ?memory|memoryerror|too many open files|cannot allocate|resource temporarily unavailable|thread ?pool|event loop|pool (is )?exhausted|stack ?overflow",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DatabasePortPattern = new(@":(5432|3306|1433|1521|27017|6379)\b", RegexOptions.CultureInvariant);

    private sealed class Finding
    {
        public string RootCause = "";
        public string Certainty = "unknown";
        public double Confidence = 0.3;
        public bool RestartFits;
        public bool StartFits;
        public string RestartReason = "";
        public List<string> NextSteps = new();
        public List<string> Alternatives = new();
        public List<string> Verification = new();
    }

    public static InvestigationResultDto Investigate(EvidencePackageDto evidence)
    {
        var signals = evidence.CorrelatedSignals;
        var metrics = signals.Select(s => s.Metric).ToHashSet(StringComparer.Ordinal);
        bool Has(params string[] names) => names.Any(metrics.Contains);
        var hasErrors = Has("errorRate", "errors");
        var hasLatency = Has("latency");
        var hasCpu = Has("cpu", "processHighResource");
        var hasMemory = Has("memory");
        var hasRetries = Has("retries");
        var hasQueue = Has("queue");
        var hasCrash = Has("processCrash");
        var hasLogPattern = Has("logPattern");

        var errorTexts = evidence.RelatedErrors
            .Select(e => $"{e.ErrorType} {e.ErrorMessage} {e.StackExcerpt} {(e.StatusCode is 429 or 502 or 503 or 504 ? e.StatusCode.ToString() : string.Empty)}")
            .Concat(evidence.EndpointBreakdown.Select(e => e.TopErrorType ?? string.Empty))
            .ToList();
        var failingDependencies = evidence.Dependencies.Where(d => d.Failures > 0 || d.AvgDurationMs >= 1000).ToList();
        var databaseDependency = failingDependencies.FirstOrDefault(d => DatabasePattern.IsMatch(d.Target) || DatabasePortPattern.IsMatch(d.Target));
        var externalDependency = failingDependencies.FirstOrDefault(d => d != databaseDependency);
        var databaseText = errorTexts.Any(t => DatabasePattern.IsMatch(t));
        var externalText = errorTexts.Any(t => ExternalPattern.IsMatch(t));
        var inProcessText = errorTexts.Any(t => InProcessStatePattern.IsMatch(t));

        var trends = evidence.RiskForecast?.Trends ?? new();
        bool Rising(string metric) => trends.Any(t => t.Metric == metric && t.Direction == "rising");
        var memoryGrowing = Rising("memory") || hasMemory;
        var onsets = evidence.SignalOnsets;
        var first = onsets.FirstOrDefault();
        var latencyLedErrors = onsets.FindIndex(o => o.Metric == "latency") is var li && li >= 0
                               && onsets.FindIndex(o => o.Metric == "errorRate") is var ei && ei > li;

        var top = evidence.EndpointBreakdown.Where(e => e.ServerErrors > 0).OrderByDescending(e => e.ServerErrors).FirstOrDefault();
        var topErrorType = top?.TopErrorType ?? evidence.RelatedErrors.Select(e => e.ErrorType).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var topMessage = evidence.RelatedErrors.Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        var stack = evidence.RelatedErrors.Select(e => e.StackExcerpt).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        var recurred = evidence.Recurrence?.RecurredAfterRestart == true;
        var failedRestart = evidence.RemediationHistory.FirstOrDefault(r => r.SameFault && r.Action.StartsWith("Restart", StringComparison.Ordinal)
                                                                           && (r.Status == "Failed" || r.Verification is "Failed"));
        var errorRateText = signals.FirstOrDefault(s => s.Metric == "errorRate") is { } er ? $"error rate {er.Observed}{er.Unit}" : "server errors";
        var onsetText = onsets.Count > 1
            ? " " + string.Join(", then ", onsets.Take(3).Select(o => $"{o.Label.ToLowerInvariant()} ({o.SecondsBeforeDetection:0}s before detection)")) + " crossed their thresholds in that order."
            : string.Empty;

        // Strongest evidence first: a measured failing dependency; then retries + errors + dependency
        // error text (a failing downstream call the app is amplifying by retrying); then saturation
        // inside the process; only then dependency inferred from error text alone.
        var kind = signals.Count == 0 ? "none"
            : hasCrash ? "crash"
            : recurred || failedRestart is not null ? "recurred"
            : databaseDependency is not null ? "database"
            : externalDependency is not null ? "external"
            : hasRetries && (databaseText || externalText) ? (databaseText ? "database" : "external")
            : hasMemory ? "memory"
            : hasCpu ? "cpu"
            : hasErrors && databaseText ? "database"
            : hasErrors && externalText ? "external"
            : hasQueue || hasRetries ? "queue"
            : hasLatency && !hasErrors ? "latency"
            : "errors";

        var f = new Finding();
        if (kind == "none")
        {
            f.RootCause = "The evidence is not sufficient to identify a cause.";
            f.NextSteps.Add("Wait for more telemetry from the app, then re-investigate.");
            f.RestartReason = "There is not enough evidence to justify changing anything.";
        }
        else if (kind == "crash")
        {
            f.RootCause = "The monitored process stopped running unexpectedly.";
            (f.Certainty, f.Confidence, f.StartFits) = ("confirmed", 0.95, true);
            f.RestartReason = "The process is not running, so there is nothing to restart; it needs starting.";
            f.NextSteps.Add("Check the application's logs for why the process exited (unhandled exception, out-of-memory kill, configuration).");
            if (memoryGrowing) f.NextSteps.Add("Memory was rising before the crash - check for memory pressure in the affected workload.");
            f.Verification.Add("The process stays running and its health checks pass for several minutes.");
        }
        else if (kind == "recurred")
        {
            f.RootCause = failedRestart is not null
                ? $"A restart for this same fault ({failedRestart.IncidentKey}) did not resolve it (status {failedRestart.Status}, verification {failedRestart.Verification ?? "not run"}), so the cause is not transient process state."
                : $"The same kind of fault returned {evidence.Recurrence!.MinutesSinceRestart:0} minute(s) after a restart, so the underlying cause is still present.";
            (f.Certainty, f.Confidence) = ("possible", 0.6);
            f.RestartReason = "A restart already failed to make this fault go away; repeating it would only hide the problem briefly.";
            f.NextSteps.Add("Investigate what persists across restarts: recent deployments or configuration changes, data the app reads, and its dependencies.");
            if (topErrorType is not null) f.NextSteps.Add($"Start with the most frequent exception, {topErrorType}{(top is not null ? $" on {top.Method} {top.Endpoint}" : "")}.");
            f.Alternatives.Add("A dependency that keeps failing would also make the fault return after every restart.");
            f.Verification.Add("The fault does not return within an hour after the underlying change is made.");
        }
        else if (kind == "database")
        {
            f.RootCause = (databaseDependency is not null
                ? $"Calls to the database ({databaseDependency.Target}) are failing or slow ({databaseDependency.Failures} of {databaseDependency.Calls} failed, avg {databaseDependency.AvgDurationMs:0} ms)"
                : "The errors are database errors" + (topErrorType is not null ? $" ({topErrorType})" : "")) +
                (latencyLedErrors ? ", and latency rose before the errors did." : ".") + onsetText;
            (f.Certainty, f.Confidence) = ("likely", 0.72);
            f.RestartReason = "The evidence points at the database; restarting the app does not make the database faster or available, and reconnecting many clients at once can add load.";
            f.NextSteps.Add("Check the database: query latency and slow-query log, connection-pool usage and waits, locks, and server CPU, memory and disk I/O.");
            f.NextSteps.Add("Only add database capacity if those measurements show it running near its limits.");
            f.Alternatives.Add("Connection-pool exhaustion in the app (too few connections for the load) can look like a slow database.");
            f.Verification.Add($"The {errorRateText} and request latency return under threshold and database call latency is back to normal.");
        }
        else if (kind == "external")
        {
            var target = externalDependency?.Target;
            f.RootCause = (target is not null
                ? $"Calls to the downstream service {target} are failing or slow ({externalDependency!.Failures} of {externalDependency.Calls} failed, avg {externalDependency.AvgDurationMs:0} ms)"
                : "Requests are failing while calling a downstream service" + (topMessage is not null ? $" (\"{topMessage}\")" : "")) +
                (hasRetries ? ", and the app is retrying those calls - rising retries multiply the load on it." : ".") + onsetText;
            (f.Certainty, f.Confidence) = (target is not null || externalText ? "likely" : "possible", target is not null ? 0.75 : 0.62);
            f.RestartReason = "The failure is in a service the app calls; restarting the app does not bring it back.";
            f.NextSteps.Add("Check the downstream service's availability and the status codes it returns (429 means rate limiting, 503/504 overload or timeouts).");
            f.NextSteps.Add("Review the app's timeout and retry settings: bounded retries with backoff, and a circuit breaker so it fails fast while the dependency is down.");
            f.Alternatives.Add("A network or DNS problem between the app and the service would show the same symptoms.");
            f.Verification.Add($"The {errorRateText} returns under threshold and retries drop back to normal while the dependency answers successfully.");
        }
        else if (kind == "queue")
        {
            f.RootCause = "Work is piling up" + (hasRetries ? " while retries rise" : "") + ": the app is receiving work faster than it completes it." + onsetText;
            (f.Certainty, f.Confidence) = ("possible", 0.58);
            f.RestartReason = "A restart would drop or delay the queued work and the backlog would rebuild for the same reason.";
            f.NextSteps.Add("Check worker capacity and processing time per item, and whether a downstream step became slower.");
            if (hasRetries) f.NextSteps.Add("Check for retry amplification: failed items retried immediately add to the queue they are waiting in.");
            f.Alternatives.Add("A burst of incoming work, rather than slower processing, produces the same backlog.");
            f.Verification.Add("Queue depth trends back down and retries return to their usual level.");
        }
        else if (kind == "memory")
        {
            f.RootCause = "The app's memory use is high" + (Rising("memory") ? " and has been growing steadily" : "") + ", which risks the process being killed." + onsetText;
            (f.Certainty, f.Confidence, f.RestartFits) = ("possible", 0.6, true);
            f.RestartReason = "A restart frees the memory and buys time as a temporary mitigation; if a leak or unbounded cache is the cause, memory will grow again.";
            f.NextSteps.Add("Find what holds the memory: caches without limits, growing collections, or a leak in the affected workload.");
            f.Alternatives.Add("A legitimate rise in load or data size, rather than a leak, also raises memory.");
            f.Verification.Add("After the change, memory levels off instead of growing over the following hour.");
        }
        else if (kind == "cpu")
        {
            f.RootCause = (hasLatency ? "CPU saturation in the app's own process is slowing its requests" : "The app's own process is saturating the CPU") + "." + onsetText;
            (f.Certainty, f.Confidence, f.RestartFits) = ("likely", 0.78, true);
            f.RestartReason = "The pressure is inside the app's process; a restart clears runaway work and restores service as a temporary mitigation. It will return if the workload or code causing it does.";
            f.NextSteps.Add("Find the CPU-heavy work (background jobs, hot loops, expensive requests) and whether the workload changed.");
            f.NextSteps.Add("If the load is legitimate, add capacity or reduce work per request; a restart does not help with that.");
            f.Alternatives.Add("A genuine traffic increase saturates the CPU the same way.");
            f.Verification.Add("CPU and latency return under threshold and stay there for several minutes.");
        }
        else if (kind == "latency")
        {
            f.RootCause = "Requests are slow without CPU pressure or errors - the app is most likely waiting on something (a dependency, lock or external call)." + onsetText;
            (f.Certainty, f.Confidence) = ("possible", 0.55);
            f.RestartReason = "Nothing shows the slowdown is inside the app's process, so a restart is unlikely to help.";
            f.NextSteps.Add("Check the latency of what the slow endpoint calls (database, downstream APIs) and for lock contention.");
            f.Alternatives.Add("A slow external service the app does not report as a dependency would look exactly like this.");
            f.Verification.Add("Latency returns under threshold.");
        }
        else
        {
            // Errors (or a logged error pattern) with normal CPU, memory and no dependency failures.
            var where = top is not null ? $" on {top.Method} {top.Endpoint}" : "";
            f.RootCause = inProcessText
                ? $"The app is failing with errors that come from its own process state ({topErrorType ?? topMessage})."
                : !hasErrors && hasLogPattern
                ? "Application logs show a repeated error pattern, most likely an unhandled exception; the evidence does not show its cause."
                : $"The app started returning {errorRateText}{where}" + (topErrorType is not null ? $", mostly {topErrorType}" : "") +
                  ", while CPU, memory and its dependencies look normal." + onsetText;
            (f.Certainty, f.Confidence) = (topErrorType is not null || topMessage is not null || hasLogPattern ? "possible" : "unknown",
                inProcessText ? 0.62 : hasLogPattern && !hasErrors ? 0.68 : 0.5);
            f.RestartFits = inProcessText;
            f.RestartReason = inProcessText
                ? "These errors come from the process's own state (resources exhausted inside it); a restart clears that state as a temporary mitigation."
                : "With normal resources and no failing dependency, the errors point at a code path, input or configuration problem; a restart would only help if the process state were corrupted, which the evidence does not show.";
            f.NextSteps.Add(topErrorType is not null
                ? $"Look at the code path that raises {topErrorType}{where}" + (stack is not null ? " (stack trace attached in the evidence)." : ".")
                : $"Find which requests fail{where} and why - the errors carry no exception detail.");
            f.NextSteps.Add("Check for a recent deployment or configuration change around the time the errors started.");
            f.Alternatives.Add("A dependency the app does not report as one (e.g. called without the SDK seeing it) could be the real source.");
            f.Verification.Add($"The {errorRateText} returns under threshold{where}.");
        }

        var offered = evidence.AvailableActions.Select(a => a.Action).ToList();
        var restart = offered.FirstOrDefault(a => a == ServiceToolNames.RestartApplication) ?? offered.FirstOrDefault(a => a == ServiceToolNames.RestartService);
        var healthCheck = offered.FirstOrDefault(a => a == ServiceToolNames.RunHealthCheck);
        var start = offered.FirstOrDefault(a => a == ServiceToolNames.StartService);

        var considered = new List<ConsideredActionDto>();
        var recommendations = new List<AiRecommendationDto>();
        if (start is not null && f.StartFits)
        {
            considered.Add(new() { Action = start, Verdict = "recommended", Reason = "The process has stopped; starting it restores the service." });
            recommendations.Add(new()
            {
                Action = start, Kind = "temporary_mitigation", RiskLevel = "medium",
                Reason = "The monitored process stopped running; starting it restores availability while the cause of the exit is investigated.",
                ExpectedOutcome = "The service runs again.",
                Risks = "If the cause of the exit is still present, it may stop again.",
                Verification = f.Verification.FirstOrDefault() ?? "KAIRON verifies recovery from fresh telemetry."
            });
        }
        if (restart is not null)
        {
            var fits = f.RestartFits && !f.StartFits;
            considered.Add(new() { Action = restart, Verdict = fits ? "recommended" : "not_recommended", Reason = f.RestartReason });
            if (fits)
            {
                recommendations.Add(new()
                {
                    Action = restart, Kind = "temporary_mitigation", RiskLevel = "medium",
                    Reason = f.RestartReason,
                    ExpectedOutcome = "The process starts fresh and the symptoms clear, if they come from in-process state.",
                    Risks = "A few seconds of downtime while the app restarts; in-flight requests fail. It does not fix the underlying cause.",
                    Verification = f.Verification.FirstOrDefault() ?? "KAIRON verifies recovery from fresh telemetry."
                });
            }
        }
        if (healthCheck is not null && recommendations.Count == 0 && signals.Count > 0)
        {
            considered.Add(new() { Action = healthCheck, Verdict = "recommended", Reason = "A read-only check of the target's state, useful while the cause is investigated." });
            recommendations.Add(new()
            {
                Action = healthCheck, Kind = "diagnostic", RiskLevel = "low",
                Reason = "Confirm the target's current state while the cause is investigated.",
                ExpectedOutcome = "Current service state; this read-only check does not repair anything.",
                Risks = "None - it changes nothing.", Verification = "Not applicable."
            });
        }

        var forecast = evidence.RiskForecast;
        var hasForecast = forecast is not null && forecast.Outcome != RiskForecastOutcomes.Inconclusive;

        return new InvestigationResultDto
        {
            Summary = $"{evidence.Incident.Service} is degraded: {string.Join("; ", evidence.Incident.Symptoms.Take(3))}",
            RootCause = f.RootCause,
            RootCauseCertainty = f.Certainty,
            ContributingFactors = signals.Select(s => s.Symptom).Take(5).ToList(),
            Evidence = signals.Select(s => $"{s.Metric} {s.Observed}{s.Unit} vs threshold {s.Threshold}{s.Unit}")
                .Concat(first is not null && onsets.Count > 1 ? new[] { $"{first.Label} crossed its threshold first, {first.SecondsBeforeDetection:0}s before detection" } : [])
                .Take(8).ToList(),
            Confidence = f.Confidence,
            Severity = evidence.Incident.Severity.ToLowerInvariant(),
            AffectedComponents = new List<string> { evidence.Incident.AffectedComponent, evidence.Incident.Service }
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList(),
            PredictedFailure = hasForecast ? forecast!.FailureMode : "Prediction inconclusive: not enough trend data.",
            EstimatedRisk = hasForecast ? forecast!.RiskLevel : "medium",
            Recommendations = recommendations,
            NextSteps = f.NextSteps,
            ConsideredActions = considered,
            AlternativeExplanations = f.Alternatives,
            MissingEvidence = evidence.EvidenceGaps.Take(4).ToList(),
            VerificationSteps = f.Verification,
            Provider = "mock",
            Model = "deterministic-mock"
        };
    }
}
