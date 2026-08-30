using Kairon.UserAgent;
using Xunit;

namespace Kairon.UserAgent.Tests;

public sealed class AgentCredentialStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kairon-credential-tests-" + Guid.NewGuid().ToString("N"), "agent-credential.json");

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

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
