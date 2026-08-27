namespace KAIRON.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";
    public string BackendEndpoint { get; set; } = "http://127.0.0.1:8000";
    public int CollectionIntervalSeconds { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 5;
    public int MaxProcesses { get; set; } = 500;
    public string IdentityPath { get; set; } = string.Empty;
}
