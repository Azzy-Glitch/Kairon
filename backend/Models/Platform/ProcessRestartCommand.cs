namespace Kairon.Backend.Models.Platform;

/// <summary>
/// One approved "restart this application process" instruction for the KAIRON UserAgent running
/// in the process owner's Windows session. Created only by the RestartApplication remediation tool
/// after operator approval and target re-validation; claimed atomically by the UserAgent of the
/// matching machine and session, which re-verifies the process (id, start time, executable) with
/// the operating system before touching it. The UserAgent relaunches the process with the command
/// line Windows reports for it - nothing here is a command to run.
/// </summary>
public sealed class ProcessRestartCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public string ActionKey { get; set; } = string.Empty;
    public Guid ProjectId { get; set; }
    public Guid MachineId { get; set; }
    public int SessionId { get; set; }
    public int ProcessId { get; set; }
    public DateTime ProcessStartedAt { get; set; }
    public string Executable { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Status { get; set; } = ProcessRestartStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? NewProcessId { get; set; }
    public string? Error { get; set; }
    public Guid RowVersion { get; set; } = Guid.NewGuid();
}

public static class ProcessRestartStatus
{
    public const string Pending = "Pending";
    public const string Claimed = "Claimed";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
}
