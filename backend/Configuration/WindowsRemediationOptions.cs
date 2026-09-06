namespace Kairon.Backend.Configuration;

public static class ProductEnvironments
{
    public static readonly string[] All = ["Development", "Staging", "Production"];
    public static bool Contains(string value) => All.Contains(value, StringComparer.OrdinalIgnoreCase);
}

public sealed class WindowsRemediationOptions
{
    public const string SectionName = "WindowsRemediation";
    public int MachineHeartbeatMaxAgeSeconds { get; set; } = 90;
    public List<WindowsServiceTarget> Targets { get; set; } = [];
}

// Operator-managed configuration, never supplied by AI or accepted from a request.
public sealed class WindowsServiceTarget
{
    public Guid ProjectId { get; set; }
    public string Environment { get; set; } = "Production";
    public string Service { get; set; } = "";
    public Guid MachineId { get; set; }
    // A dedicated project API credential provisioned to this machine/service workload.
    public Guid TelemetryCredentialId { get; set; }
    public string ExpectedHostName { get; set; } = "";
    public string WindowsServiceName { get; set; } = "";
    public List<string> AllowedOperations { get; set; } = [];
}
