namespace Kairon.Backend.Models.Sre;

public enum CorrelationOutcome
{
    None = 0,
    Created,
    /// <summary>More readings of symptoms the incident already had.</summary>
    Updated,
    /// <summary>A kind of fault the incident did not have before (e.g. errors joining a CPU incident).</summary>
    NewSymptoms,
    /// <summary>An incident detected before its telemetry was tied to a machine has now been proven,
    /// by fresh Agent-verified telemetry, to come from the remediation target's machine.</summary>
    Rescoped
}

/// <summary>
/// Groups detection metrics into kinds of fault, so "the same problem" can be told apart from a
/// different one on the same service: payment errors after an overload are a new fault, not the
/// overload continuing - and not the overload "coming back after a restart".
/// </summary>
public static class FaultCategories
{
    public static string Of(string? metric) => metric switch
    {
        "errorRate" or "errors" or "logPattern" => "errors",
        "cpu" or "processHighResource" => "cpu",
        "memory" => "memory",
        "latency" => "latency",
        "retries" => "retries",
        "queue" => "queue",
        "requests" => "traffic",
        "processCrash" => "crash",
        null or "" => "unknown",
        _ when metric.StartsWith("metric-deviation", StringComparison.Ordinal) => "anomaly",
        _ => metric
    };

    public static HashSet<string> Of(IEnumerable<CorrelatedSignalSnapshot> signals) =>
        signals.Select(s => Of(s.MetricName)).ToHashSet(StringComparer.Ordinal);
}
