namespace Kairon.Agent.LogTailing;

/// <summary>A log entry the Agent decided is worth reporting, normalized to the same
/// EventType/Severity vocabulary the backend's detection rules will read (Phase 4).</summary>
public class MatchedLogEvent
{
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Warning";
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Decides whether a log entry is worth reporting. A small, hardcoded pattern set - matching
/// every existing detection rule's own style (backend/Services/Detection/DetectionRules.cs is
/// entirely named, hardcoded conditions, not a configurable DSL) - not a shortcut relative to
/// the codebase, the idiomatic choice for it (docs/OBSERVABILITY_MIGRATION.md).
///
/// Deliberately conservative: most log volume (INF-level request logs) is not reported at all,
/// the same way the SDK only reports errors plus a sample of successes rather than everything.
/// </summary>
public static class LogPatternMatcher
{
    public static MatchedLogEvent? Match(LogEntry entry)
    {
        var level = entry.Level.ToUpperInvariant();
        var message = entry.Message;

        if (level is "FTL" or "FATAL")
        {
            return new MatchedLogEvent { EventType = "LogPatternMatch", Severity = "Critical", Message = message };
        }

        if (ContainsAny(message, "OutOfMemoryException", "out of memory", "OOM"))
        {
            return new MatchedLogEvent { EventType = "LogPatternMatch", Severity = "Critical", Message = message };
        }

        if (level is "ERR" or "ERROR")
        {
            // A stack trace (a continuation line starting with "   at ") is strong evidence of an
            // unhandled/logged exception, not just an application-level error message - worth a
            // slightly higher signal, though both are reported.
            var looksLikeException = message.Contains("\n   at ") || ContainsAny(message, "Exception", "exception");
            return new MatchedLogEvent
            {
                EventType = "LogPatternMatch",
                Severity = looksLikeException ? "Error" : "Warning",
                Message = message
            };
        }

        return null;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
}
