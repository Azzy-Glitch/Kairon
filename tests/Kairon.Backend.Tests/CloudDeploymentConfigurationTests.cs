using System.IO;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// RB-006: docker-compose.cloud.yml must never let a forgotten KAIRON_AI_PROVIDER silently start
/// the AI service on mock output - it previously defaulted to "mock" via compose's ${VAR:-default}
/// substitution, exactly like every other must-be-set secret in this file (KAIRON_SQL_CONNECTION,
/// KAIRON_OPERATOR_KEY, ...) uses ${VAR:?message} instead, which fails "docker compose up" closed.
/// A file-content check rather than actually invoking docker compose, since no Docker daemon is
/// assumed to be available wherever this test suite runs.
/// </summary>
public sealed class CloudDeploymentConfigurationTests
{
    private static string ComposeFileContent()
    {
        var path = FindRepositoryFile("docker-compose.cloud.yml");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryFile(string relativeName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativeName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativeName} above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void AiProviderHasNoSilentDefaultAndFailsClosedWhenUnset()
    {
        var content = ComposeFileContent();

        Assert.Contains("AI__Provider: ${KAIRON_AI_PROVIDER:?", content);
        Assert.DoesNotContain("AI__Provider: ${KAIRON_AI_PROVIDER:-", content);
    }

    [Theory]
    [InlineData("KAIRON_SQL_CONNECTION")]
    [InlineData("KAIRON_OPERATOR_KEY")]
    [InlineData("KAIRON_AI_SERVICE_KEY")]
    [InlineData("KAIRON_ALLOWED_ORIGIN")]
    [InlineData("KAIRON_AI_PROVIDER")]
    public void EverySecurityOrProviderCriticalVariableIsRequiredNotDefaulted(string variable)
    {
        var content = ComposeFileContent();

        Assert.Contains($"${{{variable}:?", content);
        Assert.DoesNotContain($"${{{variable}:-", content);
    }

    // --- RB-008: the backend speaks plain HTTP (backend/Dockerfile's ASPNETCORE_URLS); it must
    // never be published directly to a public interface, since the SDKs refuse a remote plaintext
    // endpoint anyway and a directly-exposed plain-HTTP backend would leak credentials/telemetry
    // in clear text over a real network. ---

    [Fact]
    public void BackendPortBindsToLoopbackByDefaultNotEveryInterface()
    {
        var content = ComposeFileContent();

        // Docker publishes to 0.0.0.0 (every interface) unless a host IP is given before the
        // port mapping - "HOST_IP:HOST_PORT:CONTAINER_PORT" - so the fix is specifically that a
        // host-IP segment is present and defaults to loopback.
        Assert.Contains("\"${KAIRON_HTTP_BIND:-127.0.0.1}:${KAIRON_HTTP_PORT:-8000}:8000\"", content);
    }

    [Fact]
    public void ATlsOverlayExistsAndPublishesStandardHttpsPorts()
    {
        var overlayPath = FindRepositoryFile("docker-compose.cloud.tls.yml");
        var overlay = File.ReadAllText(overlayPath);

        Assert.Contains("caddy", overlay, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("443:443", overlay);
        Assert.Contains("Caddyfile", overlay);
    }

    [Fact]
    public void TheCaddyfileForwardsToTheBackendOverTheInternalDockerNetworkNotAPublishedPort()
    {
        var caddyfilePath = FindRepositoryFile("deploy/Caddyfile.cloud.example");
        var caddyfile = File.ReadAllText(caddyfilePath);

        // "backend" (the compose service name) resolves only on the internal Docker network -
        // never a host-published port, which the backend service deliberately no longer exposes.
        Assert.Contains("backend:8000", caddyfile);
    }
}
