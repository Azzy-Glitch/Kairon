using System.Text;
using Microsoft.Extensions.Options;

namespace Kairon.Agent.LogTailing;

/// <summary>
/// Tails one log file incrementally - never rereads what it has already sent
/// (docs/OBSERVABILITY_MIGRATION.md: "do not repeatedly reread entire log files"). Supports
/// exactly one rotation scheme: dated daily files ("{prefix}{yyyyMMdd}.txt"), the same pattern
/// backend/Program.cs's own Serilog config uses, detected either by a new dated file appearing
/// or the current file shrinking (the truncate-in-place case). General-purpose rotation handling
/// (copy-truncate, size-based rolling, arbitrary naming) is explicitly out of scope for this
/// pass.
///
/// All parsing/matching/dedup logic lives in LogEventExtractor/LogPatternMatcher/
/// LogDeduplicator, which are pure and independently tested - this class owns only the file I/O
/// and the poll loop.
/// </summary>
public class LogTailer : BackgroundService
{
    private readonly AgentOptions _options;
    private readonly AgentEventClient _client;
    private readonly ILogger<LogTailer> _logger;
    private readonly LogDeduplicator _dedup;

    private string? _currentFilePath;
    private long _offset;
    private LogEntry? _pending;

    public LogTailer(IOptions<AgentOptions> options, AgentEventClient client, ILogger<LogTailer> logger)
    {
        _options = options.Value;
        _client = client;
        _logger = logger;
        _dedup = new LogDeduplicator(TimeSpan.FromSeconds(Math.Max(1, _options.LogDedupWindowSeconds)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableLogTailing || string.IsNullOrWhiteSpace(_options.LogDirectory))
        {
            _logger.LogInformation("kairon-agent: log tailing disabled (no LogDirectory configured)");
            return;
        }

        _logger.LogInformation(
            "kairon-agent: tailing {Directory} for files matching {Prefix}*.txt",
            _options.LogDirectory, _options.LogFilePrefix);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.LogPollIntervalSeconds)));

        do
        {
            try
            {
                await PollAsync(stoppingToken);
                _dedup.Prune(DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single bad poll (locked file, permission denied, transient disk error) must
                // never stop the Agent - the next tick tries again.
                _logger.LogWarning(ex, "kairon-agent: log poll failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var targetPath = ResolveCurrentLogFile();
        if (targetPath is null) return; // no matching file yet

        if (_currentFilePath is not null && targetPath != _currentFilePath)
        {
            // Rolled over to a new dated file - nothing more is coming for whatever was pending
            // in the old one.
            await FlushPendingAsync(cancellationToken);
            _offset = 0;
        }

        _currentFilePath = targetPath;

        var fileInfo = new FileInfo(_currentFilePath);
        if (!fileInfo.Exists) return;

        if (fileInfo.Length < _offset)
        {
            // Truncated in place - the other rotation shape this Agent recognizes.
            await FlushPendingAsync(cancellationToken);
            _offset = 0;
        }

        if (fileInfo.Length == _offset) return; // nothing new since last poll

        string newText;
        using (var stream = new FileStream(
            _currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Seek(_offset, SeekOrigin.Begin);
            // No encoding detection: this Agent supports the one format its own scenario
            // produces (Serilog's default file sink, UTF-8, no BOM), not arbitrary encodings.
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            newText = await reader.ReadToEndAsync(cancellationToken);
            _offset = stream.Position;
        }

        var (finalized, pending) = LogEventExtractor.Extract(newText, _pending);
        _pending = pending;

        await SendMatchedAsync(finalized, cancellationToken);
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending is null) return;

        var pending = _pending;
        _pending = null;
        await SendMatchedAsync(new[] { pending }, cancellationToken);
    }

    private async Task SendMatchedAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var sentThisPoll = 0;

        foreach (var entry in entries)
        {
            if (sentThisPoll >= _options.MaxLogEventsPerPoll)
            {
                _logger.LogWarning("kairon-agent: log event rate limit reached this poll, remaining entries dropped");
                break;
            }

            var matched = LogPatternMatcher.Match(entry);
            if (matched is null) continue;

            if (!_dedup.ShouldSend(matched.Message, now)) continue;

            await _client.SendAsync(new AgentEventPayload
            {
                EventType = matched.EventType,
                Severity = matched.Severity,
                Message = matched.Message,
                Source = _currentFilePath ?? string.Empty,
                Timestamp = now
            }, cancellationToken);

            sentThisPoll++;
        }
    }

    private string? ResolveCurrentLogFile()
    {
        if (string.IsNullOrWhiteSpace(_options.LogDirectory) || !Directory.Exists(_options.LogDirectory))
            return null;

        var todayPath = Path.Combine(_options.LogDirectory, $"{_options.LogFilePrefix}{DateTime.UtcNow:yyyyMMdd}.txt");
        if (File.Exists(todayPath)) return todayPath;

        // Not created yet today (e.g. right after a day boundary, before the target app's first
        // log line) - fall back to the newest dated file so the Agent does not go blind.
        return Directory.GetFiles(_options.LogDirectory, $"{_options.LogFilePrefix}*.txt")
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }
}
