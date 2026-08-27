namespace Kairon.Agent.LogTailing;

/// <summary>
/// Suppresses a repeated (identical) log message within a rolling window, so a tight failure
/// loop - the same exception on every request while a retry storm is active - produces one
/// event, not one per occurrence. This is the rate-limiting half of "the log collector must not
/// repeatedly reread entire log files [or] flood the backend" - the incremental-read half lives
/// in LogTailer.
///
/// Deliberately simple: the first occurrence of a message in the window is sent; every repeat
/// within the window is silently absorbed. It does not retroactively update the count on an
/// already-sent event - the goal is protecting the backend from flooding, not a perfectly
/// accurate running tally.
/// </summary>
public class LogDeduplicator
{
    private readonly TimeSpan _window;
    private readonly Dictionary<string, DateTime> _lastSent = new();

    public LogDeduplicator(TimeSpan window)
    {
        _window = window;
    }

    public bool ShouldSend(string key, DateTime now)
    {
        if (_lastSent.TryGetValue(key, out var last) && now - last <= _window)
            return false;

        _lastSent[key] = now;
        return true;
    }

    /// <summary>Drops entries whose window has already expired, so a long-running Agent process
    /// does not accumulate one dictionary entry per distinct message forever.</summary>
    public void Prune(DateTime now)
    {
        List<string>? expired = null;

        foreach (var (key, lastSeen) in _lastSent)
        {
            if (now - lastSeen > _window)
            {
                expired ??= new List<string>();
                expired.Add(key);
            }
        }

        if (expired is null) return;
        foreach (var key in expired) _lastSent.Remove(key);
    }
}
