using System.Diagnostics;

namespace Kairon.UserAgent;

/// <summary>
/// Production IProcessSnapshotSource: enumerates only the processes in this UserAgent's own
/// Windows session - the exact boundary a LocalService-based Windows Service cannot cross.
/// GetCandidateProcessIds itself never throws for an individual process (Process.GetProcesses()
/// and reading .Id/.SessionId are cheap and reliable); the properties that can legitimately fail
/// for an inaccessible or exiting process (StartTime, TotalProcessorTime, MainModule.FileName) are
/// deferred to ReadSample, which SessionProcessCollector.CollectSamples isolates per-process.
/// </summary>
public sealed class SystemProcessSnapshotSource : IProcessSnapshotSource, IDisposable
{
    private readonly int _sessionId;
    private readonly Dictionary<int, Process> _processes = new();

    public SystemProcessSnapshotSource(int sessionId) => _sessionId = sessionId;

    public IReadOnlyList<int> GetCandidateProcessIds()
    {
        Dispose();

        var ids = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId == _sessionId)
                {
                    _processes[process.Id] = process;
                    ids.Add(process.Id);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                // SessionId itself can throw for a small number of protected system processes -
                // just exclude them, same fail-open philosophy as everywhere else in the Agent.
                process.Dispose();
            }
        }

        return ids;
    }

    public RawProcessSample ReadSample(int processId)
    {
        if (!_processes.TryGetValue(processId, out var process))
            throw new InvalidOperationException($"Process {processId} is no longer available.");

        process.Refresh();
        return new RawProcessSample(process.Id, process.StartTime.ToUniversalTime(), process.ProcessName,
            SafeFileName(process), process.TotalProcessorTime, process.WorkingSet64);
    }

    private static string SafeFileName(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Access-denied on an elevated or otherwise-protected process - not fatal, just no path.
            return string.Empty;
        }
    }

    public void Dispose()
    {
        foreach (var process in _processes.Values) process.Dispose();
        _processes.Clear();
    }
}
