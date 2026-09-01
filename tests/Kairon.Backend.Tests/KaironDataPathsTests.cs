using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class KaironDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kairon-data-path-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitDatabaseLayoutCreatesAWritableSiblingLogDirectory()
    {
        var paths = KaironDataPaths.Resolve(new PersistenceOptions
        {
            DatabasePath = Path.Combine(_root, "data", "test.db")
        });

        paths.EnsureLogsCreated();

        Assert.Equal(Path.Combine(_root, "data", "logs"), paths.Logs);
        Assert.True(Directory.Exists(paths.Logs));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
