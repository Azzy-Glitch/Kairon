using Kairon.Agent;
using Xunit;

namespace Kairon.Agent.Tests;

public sealed class AgentCredentialStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kairon-credential-tests-" + Guid.NewGuid().ToString("N"), "agent-credential.json");
    private string UserPath => Path.Combine(Path.GetDirectoryName(_path)!, "useragent-credential.json");

    [Fact]
    public void ExplicitNonDefaultKeyIsHonoredUnchanged()
    {
        var result = AgentCredentialStore.Resolve("my-custom-test-key", _path);

        Assert.Equal("my-custom-test-key", result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void InsecureDefaultTriggersGenerationAndPersistence()
    {
        var generated = AgentCredentialStore.Resolve(AgentCredentialStore.InsecureDefaultAgentKey, _path);

        Assert.NotEqual(AgentCredentialStore.InsecureDefaultAgentKey, generated);
        Assert.True(generated.Length > 20);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void SecondCallReusesThePersistedKeyRatherThanGeneratingANewOne()
    {
        var first = AgentCredentialStore.Resolve(AgentCredentialStore.InsecureDefaultAgentKey, _path);
        var second = AgentCredentialStore.Resolve(AgentCredentialStore.InsecureDefaultAgentKey, _path);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EmptyConfiguredKeyAlsoTriggersGeneration()
    {
        var generated = AgentCredentialStore.Resolve(string.Empty, _path);

        Assert.NotEmpty(generated);
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void FreshCredentialBundleUsesDistinctMachineAndUserAgentSecrets()
    {
        var credentials = AgentCredentialStore.ResolveCredentials(
            AgentCredentialStore.InsecureDefaultAgentKey,
            AgentCredentialStore.InsecureDefaultUserAgentKey,
            _path,
            UserPath);

        Assert.NotEqual(credentials.AgentKey, credentials.UserAgentKey);
        Assert.Null(credentials.PreviousAgentKey);
        Assert.True(File.Exists(_path));
        Assert.True(File.Exists(UserPath));
    }

    [Fact]
    public void LegacySharedCredentialIsRotatedAndRetainedOnlyUntilRegistrationCompletes()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"AgentKey\":\"legacy-shared-key-that-is-long-enough\"}");

        var migrated = AgentCredentialStore.ResolveCredentials(
            AgentCredentialStore.InsecureDefaultAgentKey,
            AgentCredentialStore.InsecureDefaultUserAgentKey,
            _path,
            UserPath);

        Assert.Equal("legacy-shared-key-that-is-long-enough", migrated.PreviousAgentKey);
        Assert.NotEqual(migrated.PreviousAgentKey, migrated.AgentKey);

        AgentCredentialStore.CompleteRotation(_path);
        var afterRegistration = AgentCredentialStore.ResolveCredentials(
            AgentCredentialStore.InsecureDefaultAgentKey,
            AgentCredentialStore.InsecureDefaultUserAgentKey,
            _path,
            UserPath);

        Assert.Null(afterRegistration.PreviousAgentKey);
        Assert.Equal(migrated.AgentKey, afterRegistration.AgentKey);
    }

    // --- NB-001: credential writes are atomic (temp file -> flush -> replace), never a direct
    // in-place write that could leave a truncated/corrupted credential active. ---

    [Fact]
    public void NoTemporaryFileIsLeftBehindAfterASuccessfulWrite()
    {
        AgentCredentialStore.Resolve(AgentCredentialStore.InsecureDefaultAgentKey, _path);

        var strayTempFiles = Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.tmp");
        Assert.Empty(strayTempFiles);
    }

    [Fact]
    public void AFailedRotationLeavesThePreviousValidCredentialFileCompletelyUnchanged()
    {
        // Seed a legacy v1 credential - exactly the shape ResolveCredentials rewrites to v2 on its
        // first call (see LegacySharedCredentialIsRotatedAndRetainedOnlyUntilRegistrationCompletes
        // above), so this exercises a genuine "replace an existing file" write, not just a
        // create-new one.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        const string originalContent = "{\"AgentKey\":\"legacy-shared-key-that-is-long-enough\"}";
        File.WriteAllText(_path, originalContent);

        // A real, unmocked failure: an open handle without FileShare.Delete prevents Windows from
        // completing the atomic rename over this exact path, exactly like a genuine interruption
        // (another process briefly holding the file, an AV scanner, a backup tool) would.
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => AgentCredentialStore.ResolveCredentials(
                AgentCredentialStore.InsecureDefaultAgentKey,
                AgentCredentialStore.InsecureDefaultUserAgentKey,
                _path,
                UserPath));
        }

        // The original file - never opened for writing on this path, only ever the temp file was -
        // is byte-for-byte exactly what it was before the failed attempt.
        Assert.Equal(originalContent, File.ReadAllText(_path));

        // No stray temp file left behind by the failed attempt either.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.tmp"));

        // And a subsequent, unobstructed call still succeeds normally and can still migrate it.
        var recovered = AgentCredentialStore.ResolveCredentials(
            AgentCredentialStore.InsecureDefaultAgentKey,
            AgentCredentialStore.InsecureDefaultUserAgentKey,
            _path,
            UserPath);
        Assert.Equal("legacy-shared-key-that-is-long-enough", recovered.PreviousAgentKey);
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
