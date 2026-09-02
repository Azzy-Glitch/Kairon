using System.Text.Json;

namespace Kairon.UserAgent;

/// <summary>
/// Reads the lower-privilege credential created by KAIRON.Agent for interactive-session
/// heartbeats. It never reads or creates the Windows service's machine credential.
/// </summary>
public static class AgentCredentialStore
{
    public const string InsecureDefaultAgentKey = "kairon-useragent-default-key-change-me";

    /// <summary>
    /// Runtime lookup used by the UserAgent. Missing/unreadable credentials fail closed and are
    /// retried on the next heartbeat; the public checked-in default is never transmitted.
    /// </summary>
    public static string? TryResolve(string configuredKey) =>
        TryResolve(configuredKey, DefaultCredentialPath());

    public static string? TryResolve(string configuredKey, string credentialPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey) && configuredKey != InsecureDefaultAgentKey)
            return configuredKey;

        try
        {
            if (!File.Exists(credentialPath)) return null;
            var existing = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(credentialPath));
            return string.IsNullOrWhiteSpace(existing?.AgentKey) ? null : existing.AgentKey;
        }
        catch
        {
            return null;
        }
    }

    private static string DefaultCredentialPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Kairon", "config", "useragent-credential.json");

    private sealed class StoredCredential
    {
        public string AgentKey { get; set; } = string.Empty;
    }
}
