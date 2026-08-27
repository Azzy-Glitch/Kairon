namespace AIDIP.Backend.Models;

public class Machine
{
    public Guid Id { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string AgentCredentialHash { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public ICollection<DiscoveredApplication> Applications { get; set; } = new List<DiscoveredApplication>();
}
