using System.Text;
using System.Text.RegularExpressions;

namespace Kairon.Agent.LogTailing;

/// <summary>One log entry: a Serilog "new entry" line plus any continuation lines that followed
/// it (a stack trace, a multi-line exception) before the next timestamped line.</summary>
public class LogEntry
{
    public string Level { get; set; } = "INF";
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Turns raw log text into grouped entries. Supports exactly the one format this Agent's
/// scenario produces - Serilog's own default file template, "{timestamp} {offset} [{LVL}]
/// {message}" - confirmed against real output in backend/logs/*.txt, not guessed
/// (docs/OBSERVABILITY_MIGRATION.md: one rotation/format scheme, not a general parser). A line
/// that does not start with that timestamp shape is a continuation of the previous entry - this
/// is what lets a multi-line stack trace collapse into the same event as the error line above it.
///
/// Pure and stateless: the tailer owns file I/O and byte offsets, this only ever sees text.
/// </summary>
public static class LogEventExtractor
{
    private static readonly Regex EntryStart = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[(\w{3})\]\s?(.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Splits <paramref name="text"/> into finalized entries plus one possibly-incomplete
    /// trailing entry. The trailing entry is not "done" - the writer may still be appending
    /// continuation lines to it - so the caller should hold it and prepend it to the next poll's
    /// text rather than sending it immediately. Any <paramref name="pending"/> entry carried over
    /// from a previous call is treated as already-started, so its continuation lines keep
    /// accumulating across polls instead of being lost at a poll boundary.
    /// </summary>
    public static (IReadOnlyList<LogEntry> Finalized, LogEntry? Pending) Extract(string text, LogEntry? pending)
    {
        var finalized = new List<LogEntry>();
        var current = pending;
        var continuation = new StringBuilder();

        void CommitContinuationInto(LogEntry entry)
        {
            if (continuation.Length > 0)
            {
                entry.Message = entry.Message.Length == 0
                    ? continuation.ToString()
                    : $"{entry.Message}\n{continuation}";
                continuation.Clear();
            }
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = EntryStart.Match(line);

            if (match.Success)
            {
                if (current is not null)
                {
                    CommitContinuationInto(current);
                    finalized.Add(current);
                }

                current = new LogEntry { Level = match.Groups[1].Value, Message = match.Groups[2].Value };
            }
            else if (current is not null && line.Length > 0)
            {
                if (continuation.Length > 0) continuation.Append('\n');
                continuation.Append(line);
            }
            // Text before the first recognized entry-start (with no pending entry to attach to)
            // is genuinely unattributable and is dropped - this only happens on the very first
            // read of a log file that starts mid-entry, which cannot occur for a file the Agent
            // has watched from its own first line.
        }

        if (current is not null)
        {
            CommitContinuationInto(current);
        }

        // The last entry might still be growing (more continuation lines could arrive on the
        // next poll) - unless the text consumed ended in a newline, in which case Serilog had
        // already moved on to writing something else and this entry is genuinely done.
        var endedCleanly = text.Length > 0 && text[^1] == '\n';

        if (current is not null && !endedCleanly)
        {
            return (finalized, current);
        }

        if (current is not null)
        {
            finalized.Add(current);
        }

        return (finalized, null);
    }
}
