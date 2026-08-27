namespace KAIRON.Agent;

public sealed record RegistrationRequest(Guid MachineId, string HostName, string OperatingSystem,
    string Architecture, string AgentVersion, string AgentKey);
public sealed record HeartbeatRequest(DateTime Timestamp, IReadOnlyList<ProcessSnapshot> Processes);
public sealed record ProcessSnapshot(int ProcessId, DateTime StartedAt, string Name, string Executable,
    string Runtime, double CpuPercent, long MemoryBytes);
