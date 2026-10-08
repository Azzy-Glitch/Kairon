using Kairon.Desktop;
using Xunit;

namespace Kairon.Desktop.Tests;

public sealed class OperatorKeySourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairon-desktop-key-" + Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_dir, "operator.key");

    public OperatorKeySourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task MissingKeyIsReportedAsUnavailableRatherThanThrowing()
    {
        var source = new OperatorKeySource(KeyPath);
        Assert.Null(source.Current);
        Assert.False(await source.WaitAvailableAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public void AServiceRestartRotatesTheKeyAndTheOpenWindowFollows()
    {
        File.WriteAllText(KeyPath, "FIRST\n");
        var source = new OperatorKeySource(KeyPath);
        Assert.Equal("FIRST", source.Current);

        File.WriteAllText(KeyPath, "SECOND");
        File.SetLastWriteTimeUtc(KeyPath, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal("SECOND", source.Current);
    }

    [Fact]
    public void OperatorKeyLivesUnderTheServiceDataRoot() =>
        Assert.EndsWith(Path.Combine("Kairon", "backend", "operator", "operator.key"), BackendService.OperatorKeyPath());
}
