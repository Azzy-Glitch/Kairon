using System.Security.Cryptography;
using System.Text.Json;

namespace Kairon.Agent;

/// <summary>
/// Resolves a real, per-installation AgentKey instead of the checked-in default
/// ("kairon-agent-default-key-change-me", identical across every install and public in source -
/// see AgentOptions.AgentKey). An explicitly configured non-default key is honored unchanged
/// (backward compatible with manual overrides and test fixtures). Otherwise this reads/generates
/// a random key persisted at %ProgramData%\Kairon\config\agent-credential.json - a machine-wide
/// location (not %LOCALAPPDATA%, which differs per user) so both the LocalService-run Agent and
/// the interactive-user-run UserAgent land on the identical key automatically, with no manual
/// step. installer/Kairon.iss already creates this exact directory with users-modify permissions
/// for unrelated reasons; this is the first thing that actually uses it.
///
/// Duplicated in agent/Kairon.UserAgent/AgentCredentialStore.cs (same reasoning as
/// MachineRegistrationService.DeriveMachineId's duplication there: small enough that a shared
/// library project for one file isn't worth the extra solution wiring). Both copies must stay
/// byte-for-byte identical - covered by a parity test in each Tests project.
/// </summary>
public static class AgentCredentialStore
{
    public const string InsecureDefaultAgentKey = "kairon-agent-default-key-change-me";

    public static string Resolve(string configuredKey) => Resolve(configuredKey, DefaultCredentialPath());

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
        catch
        {
            // Fail open to the checked-in default rather than crash outright - a shared
            // machine-wide file becoming unreadable/unwritable (permissions, disk issues) must not
            // stop machine telemetry entirely; this install just keeps using the known-weak
            // default until the underlying issue is fixed, exactly as before this change existed.
            return configuredKey;
        }
    }

    private static string DefaultCredentialPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Kairon", "config", "agent-credential.json");

    private sealed class StoredCredential
    {
        public string AgentKey { get; set; } = string.Empty;
    }
}
