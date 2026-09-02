using System.Security.Cryptography;
using System.Text.Json;

namespace Kairon.Agent;

/// <summary>
/// Resolves a real, per-installation AgentKey instead of the checked-in default
/// ("kairon-agent-default-key-change-me", identical across every install and public in source -
/// see AgentOptions.AgentKey). An explicitly configured non-default key is honored unchanged
/// (backward compatible with manual overrides and test fixtures). Otherwise this reads/generates
/// a random key persisted at %ProgramData%\Kairon\config\agent-credential.json. A second, scoped
/// key is stored in useragent-credential.json. The installer ACL permits interactive users to
/// read only that lower-privilege key; the LocalService machine key remains readable solely by
/// SYSTEM, Administrators, and LocalService.
/// </summary>
public static class AgentCredentialStore
{
    public const string InsecureDefaultAgentKey = "kairon-agent-default-key-change-me";
    public const string InsecureDefaultUserAgentKey = "kairon-useragent-default-key-change-me";

    public sealed record ResolvedCredentials(
        string AgentKey,
        string UserAgentKey,
        string? PreviousAgentKey);

    public static string Resolve(string configuredKey) => Resolve(configuredKey, DefaultCredentialPath());

    public static ResolvedCredentials ResolveCredentials(
        string configuredAgentKey,
        string configuredUserAgentKey) =>
        ResolveCredentials(
            configuredAgentKey,
            configuredUserAgentKey,
            DefaultCredentialPath(),
            DefaultUserAgentCredentialPath());

    /// <summary>
    /// Resolves independent service/UserAgent keys. A legacy v1 file is rotated once and retains
    /// the previous key until the backend confirms registration, so an offline backend cannot
    /// strand an installation between local and persisted credentials.
    /// </summary>
    public static ResolvedCredentials ResolveCredentials(
        string configuredAgentKey,
        string configuredUserAgentKey,
        string agentCredentialPath,
        string userAgentCredentialPath)
    {
        var agentKey = configuredAgentKey;
        string? previousAgentKey = null;

        if (string.IsNullOrWhiteSpace(agentKey) || agentKey == InsecureDefaultAgentKey)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(agentCredentialPath)!);
            var stored = Read(agentCredentialPath);

            if (stored is { Version: >= 2 } && !string.IsNullOrWhiteSpace(stored.AgentKey))
            {
                agentKey = stored.AgentKey;
                previousAgentKey = stored.PreviousAgentKey;
            }
            else
            {
                previousAgentKey = string.IsNullOrWhiteSpace(stored?.AgentKey) ? null : stored.AgentKey;
                agentKey = Generate();
                Write(agentCredentialPath, new StoredCredential
                {
                    Version = 2,
                    AgentKey = agentKey,
                    PreviousAgentKey = previousAgentKey
                });
            }
        }

        var userAgentKey = configuredUserAgentKey;
        if (string.IsNullOrWhiteSpace(userAgentKey) || userAgentKey == InsecureDefaultUserAgentKey)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(userAgentCredentialPath)!);
            var stored = Read(userAgentCredentialPath);
            userAgentKey = !string.IsNullOrWhiteSpace(stored?.AgentKey) ? stored.AgentKey : Generate();
            if (stored is null || string.IsNullOrWhiteSpace(stored.AgentKey))
            {
                Write(userAgentCredentialPath, new StoredCredential
                {
                    Version = 2,
                    AgentKey = userAgentKey
                });
            }
        }

        return new ResolvedCredentials(agentKey, userAgentKey, previousAgentKey);
    }

    public static void CompleteRotation() => CompleteRotation(DefaultCredentialPath());

    public static void CompleteRotation(string agentCredentialPath)
    {
        try
        {
            var stored = Read(agentCredentialPath);
            if (stored is not { Version: >= 2 } || string.IsNullOrWhiteSpace(stored.PreviousAgentKey))
                return;

            stored.PreviousAgentKey = null;
            Write(agentCredentialPath, stored);
        }
        catch
        {
            // The new credential is already registered. Retaining the protected previous value is
            // harmless and lets the next successful registration retry the cleanup.
        }
    }

    /// <summary>Path parameter exposed for tests - production callers always use the single-arg
    /// overload.</summary>
    public static string Resolve(string configuredKey, string credentialPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey) && configuredKey != InsecureDefaultAgentKey)
            return configuredKey;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
            if (File.Exists(credentialPath))
            {
                var existing = JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(credentialPath));
                if (!string.IsNullOrWhiteSpace(existing?.AgentKey)) return existing.AgentKey;
            }

            var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(credentialPath, JsonSerializer.Serialize(new StoredCredential { AgentKey = generated }));
            return generated;
        }
        catch (Exception ex)
        {
            // Authentication must fail closed. Falling back to a public checked-in value would
            // make a filesystem or ACL fault silently downgrade machine identity security.
            throw new InvalidOperationException("The Agent credential could not be securely resolved.", ex);
        }
    }

    private static string DefaultCredentialPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Kairon", "config", "agent-credential.json");

    private static string DefaultUserAgentCredentialPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Kairon", "config", "useragent-credential.json");

    private static StoredCredential? Read(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<StoredCredential>(File.ReadAllText(path));
    }

    private static void Write(string path, StoredCredential credential) =>
        File.WriteAllText(path, JsonSerializer.Serialize(credential));

    private static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private sealed class StoredCredential
    {
        public int Version { get; set; } = 1;
        public string AgentKey { get; set; } = string.Empty;
        public string? PreviousAgentKey { get; set; }
    }
}
