namespace Kairon.UserAgent;

/// <summary>A raw per-process reading, before CPU-percent enrichment (that needs a baseline from
/// the previous poll - see CpuBaselineCache) or parent-PID enrichment (see
/// ToolhelpProcessSnapshot).</summary>
public sealed record RawProcessSample(int ProcessId, DateTime StartedAt, string Name, string Executable,
    TimeSpan TotalProcessorTime, long MemoryBytes);

/// <summary>
/// Abstraction over reading one process's metadata, so the fail-open orchestration in
/// SessionProcessCollector.CollectSamples is unit-testable without real OS processes - including
/// the case that matters most here: one process legitimately throwing (access denied, exited
/// mid-read) while the rest are read successfully.
/// </summary>
public interface IProcessSnapshotSource
{
    IReadOnlyList<int> GetCandidateProcessIds();

    /// <summary>May throw for a single process id - callers must isolate that failure to this one
    /// process, never the whole batch.</summary>
    RawProcessSample ReadSample(int processId);
}
