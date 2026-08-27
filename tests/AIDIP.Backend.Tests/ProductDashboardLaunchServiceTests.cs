using AIDIP.Backend.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AIDIP.Backend.Tests;

public class ProductDashboardLaunchServiceTests
{
    [Fact]
    public void ServerModeDoesNotOpenAnInteractiveDashboard()
    {
        var configuration = BuildConfiguration("http://127.0.0.1:8000");

        var result = ProductDashboardLaunchService.ResolveDashboardUrl([], configuration);

        Assert.Null(result);
    }

    [Fact]
    public void DesktopModeUsesConfiguredLocalDashboardUrl()
    {
        var configuration = BuildConfiguration("http://127.0.0.1:8123");

        var result = ProductDashboardLaunchService.ResolveDashboardUrl(["KAIRON.exe", "--desktop"], configuration);

        Assert.Equal(new Uri("http://127.0.0.1:8123"), result);
    }

    [Theory]
    [InlineData("file:///C:/secrets.txt")]
    [InlineData("not-a-url")]
    public void DesktopModeRejectsUnsafeOrInvalidDashboardUrls(string value)
    {
        var configuration = BuildConfiguration(value);

        Assert.Throws<InvalidOperationException>(() =>
            ProductDashboardLaunchService.ResolveDashboardUrl(["--desktop"], configuration));
    }

    private static IConfiguration BuildConfiguration(string url) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Product:DashboardUrl"] = url })
            .Build();
}
