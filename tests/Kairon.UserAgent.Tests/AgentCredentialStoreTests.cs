using Kairon.UserAgent;
using Xunit;

namespace Kairon.UserAgent.Tests;

public sealed class AgentCredentialStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kairon-credential-tests-" + Guid.NewGuid().ToString("N"), "agent-credential.json");

    [Fact]
    public void ExplicitNonDefaultKeyIsHonoredUnchanged()
    {
        var result = AgentCredentialStore.TryResolve("my-custom-test-key", _path);

        Assert.Equal("my-custom-test-key", result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void MissingScopedCredentialFailsClosedWithoutCreatingAFile()
    {
        var result = AgentCredentialStore.TryResolve(AgentCredentialStore.InsecureDefaultAgentKey, _path);

        Assert.Null(result);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void ReadsTheScopedCredentialCreatedByTheWindowsService()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"AgentKey\":\"generated-useragent-key-that-is-long-enough\"}");

        var result = AgentCredentialStore.TryResolve(AgentCredentialStore.InsecureDefaultAgentKey, _path);

        Assert.Equal("generated-useragent-key-that-is-long-enough", result);
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
