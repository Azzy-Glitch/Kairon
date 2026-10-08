using System.Security.Cryptography;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// Operator-key handoff for the Kairon.Backend Windows service. In desktop/dev mode the shell
/// generated a per-launch key and passed it to its child backend through the environment; a
/// Windows service has no such parent. Instead the service generates a fresh 256-bit key at every
/// start, publishes it atomically to a file whose folder ACL the installer restricts (SYSTEM,
/// Administrators, the service account, and interactively logged-on users read-only), and the
/// desktop shell reads it and injects it at the WebView2 network boundary exactly as before.
/// A restart rotates the key; nothing else is persisted.
/// </summary>
public static class OperatorKeyFile
{
    public static string Provision(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("SreSecurity:OperatorKeyFile must have a parent directory.");
        Directory.CreateDirectory(directory);

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // Write a sibling temp file and atomically replace, so a reader never observes a partial
        // key. The temp file inherits the same restricted folder ACL as the final file.
        var temp = Path.Combine(directory, $".operator-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, key);
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return key;
    }
}
