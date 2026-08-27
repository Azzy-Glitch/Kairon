namespace AIDIP.Backend.Configuration;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";
    public string Provider { get; set; } = "SQLite";
    public string DatabasePath { get; set; } = string.Empty;
}
