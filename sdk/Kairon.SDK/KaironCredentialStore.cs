using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Kairon.SDK;

/// <summary>
/// Persists the project credential obtained from a one-time pairing code, so a paired application
/// does not need to pair again on every restart (docs/DESKTOP_SHELL.md pairing flow).
///
/// This is deliberately a separate, new mechanism from Agent\Kairon.Agent's AgentCredentialStore:
/// that one is installer-managed, machine-wide (%ProgramData%), and relies on Windows ACLs set up
/// during installation to protect an otherwise-plaintext file - not available to an arbitrary,
/// non-elevated application process with no installer step of its own. Instead this reuses the
/// same ASP.NET Core Data Protection API the backend already uses to encrypt its own stored
/// secrets (backend/Services/AiProviderConfigService.cs, DatabaseConfigurationService.cs),
/// DPAPI-wrapping the key ring on Windows, so the stored credential is never plaintext on disk.
/// Kairon.SDK already references the full ASP.NET Core shared framework (FrameworkReference in
/// Kairon.SDK.csproj), so this needs no new NuGet dependency.
/// </summary>
internal static class KaironCredentialStore
{
    private const string ProtectorPurpose = "Kairon.SDK.PairedCredential.v1";

    private sealed class StoredCredential
    {
        public string Endpoint { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string ApiKey { get; set; } = "";

        /// <summary>Present only while a redeemed credential has not yet been confirmed with the
        /// backend (the confirmation response was lost, or the process exited before sending it).
        /// Non-secret (a session id, not a credential) - kept purely so a later run can retry
        /// confirming it without needing a new pairing code.</summary>
        public string? PendingConfirmationPairingId { get; set; }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kairon", "sdk", "credential.json");

    public static (string Endpoint, Guid ProjectId, string ApiKey, Guid? PendingConfirmationPairingId)? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = CreateProtector(path).Unprotect(File.ReadAllText(path));
            var stored = JsonSerializer.Deserialize<StoredCredential>(json);
            if (stored is null || !Guid.TryParse(stored.ProjectId, out var projectId) ||
                string.IsNullOrWhiteSpace(stored.ApiKey) || string.IsNullOrWhiteSpace(stored.Endpoint))
                return null;
            Guid? pending = Guid.TryParse(stored.PendingConfirmationPairingId, out var pendingId) ? pendingId : null;
            return (stored.Endpoint, projectId, stored.ApiKey, pending);
        }
        catch
        {
            // Corrupted, foreign-machine-encrypted (a different DPAPI user/machine key), or
            // otherwise unreadable: treat exactly like "not paired yet" rather than crashing
            // startup. The caller re-pairs if a pairing code is available.
            return null;
        }
    }

    /// <summary>Throws on failure rather than returning a status - a caller must never report a
    /// successful pairing when the credential could not actually be persisted.</summary>
    public static void Save(string path, string endpoint, Guid projectId, string apiKey, Guid? pendingConfirmationPairingId = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new StoredCredential
        {
            Endpoint = endpoint,
            ProjectId = projectId.ToString(),
            ApiKey = apiKey,
            PendingConfirmationPairingId = pendingConfirmationPairingId?.ToString()
        });
        var protectedText = CreateProtector(path).Protect(json);

        // A uniquely-named temp file per call (not a single fixed ".tmp") - two SDK instances (or
        // two overlapping calls in one process) saving at the same moment must never read or
        // clobber each other's still-being-written temp file; File.Move is still the sole atomic
        // publish step either way, so whichever finishes last still wins cleanly.
        var tempPath = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, protectedText);
            // On Windows, a concurrent replace of the SAME destination from another thread/process
            // (or a virus scanner briefly holding it open) can transiently fail with
            // IOException/UnauthorizedAccessException - a short bounded retry is the standard way
            // to make the move itself robust to that; it remains the one atomic publish step.
            Exception? lastError = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex;
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
            if (lastError is not null) throw lastError;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static IDataProtector CreateProtector(string credentialPath)
    {
        var keyRingPath = Path.Combine(Path.GetDirectoryName(credentialPath)!, "keys");
        Directory.CreateDirectory(keyRingPath);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRingPath), b =>
        {
            b.SetApplicationName("Kairon");
            if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi();
        });
        return provider.CreateProtector(ProtectorPurpose);
    }
}
