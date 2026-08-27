using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace KAIRON.Agent;

public sealed class ProcessCollector
{
    private readonly int _maximum;
    private readonly Dictionary<(int Id, DateTime Started), (TimeSpan Cpu, DateTime Sampled)> _previous = [];

    public ProcessCollector(IOptions<AgentOptions> options) => _maximum = Math.Clamp(options.Value.MaxProcesses, 1, 500);

    public IReadOnlyList<ProcessSnapshot> Collect()
    {
        var now = DateTime.UtcNow;
        var snapshots = new List<ProcessSnapshot>(_maximum);
        var currentKeys = new HashSet<(int, DateTime)>();
        foreach (var process in Process.GetProcesses().OrderBy(p => p.Id))
        {
            if (snapshots.Count >= _maximum) { process.Dispose(); break; }
            using (process)
            {
                try
                {
                    var started = process.StartTime.ToUniversalTime();
                    var key = (process.Id, started);
                    var totalCpu = process.TotalProcessorTime;
                    var cpu = 0d;
                    if (_previous.TryGetValue(key, out var previous))
                    {
                        var elapsed = (now - previous.Sampled).TotalMilliseconds;
                        if (elapsed > 0)
                            cpu = (totalCpu - previous.Cpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100;
                    }
                    _previous[key] = (totalCpu, now);
                    currentKeys.Add(key);
                    snapshots.Add(new ProcessSnapshot(process.Id, started, SafeName(process), SafeExecutable(process),
                        DetectRuntime(process), Math.Clamp(cpu, 0, 100), Math.Max(0, process.WorkingSet64)));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Processes may exit or become inaccessible during enumeration. Monitoring must continue.
                }
            }
        }

        foreach (var stale in _previous.Keys.Where(key => !currentKeys.Contains(key)).ToArray()) _previous.Remove(stale);
        return snapshots;
    }

    private static string SafeName(Process process) { try { return process.ProcessName; } catch { return $"pid-{process.Id}"; } }
    private static string SafeExecutable(Process process) { try { return process.MainModule?.FileName ?? string.Empty; } catch { return string.Empty; } }
    private static string DetectRuntime(Process process)
    {
        var name = SafeName(process).ToLowerInvariant();
        if (name is "dotnet" or "iisexpress" || name.EndsWith(".vshost")) return ".NET";
        if (name.StartsWith("python") || name is "py") return "Python";
        if (name is "node") return "Node.js";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && name is "java" or "javaw") return "Java";
        return "Native/Unknown";
    }
}
