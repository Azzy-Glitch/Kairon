using System.Diagnostics;

namespace AIDIP.Backend.Services;

public interface IProductUrlLauncher
{
    void Open(Uri url);
}

public sealed class ProductUrlLauncher : IProductUrlLauncher
{
    public void Open(Uri url)
    {
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>
/// Opens the bundled product UI after the local control plane is listening. This is deliberately
/// enabled by an explicit desktop argument so services, tests, and server deployments never open
/// an interactive browser unexpectedly.
/// </summary>
public sealed class ProductDashboardLaunchService : IHostedService
{
    public const string DesktopArgument = "--desktop";

    private readonly IHostApplicationLifetime _lifetime;
    private readonly IProductUrlLauncher _launcher;
    private readonly ILogger<ProductDashboardLaunchService> _logger;
    private readonly Uri? _dashboardUrl;

    public ProductDashboardLaunchService(
        IHostApplicationLifetime lifetime,
        IProductUrlLauncher launcher,
        IConfiguration configuration,
        ILogger<ProductDashboardLaunchService> logger)
    {
        _lifetime = lifetime;
        _launcher = launcher;
        _logger = logger;
        _dashboardUrl = ResolveDashboardUrl(Environment.GetCommandLineArgs(), configuration);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_dashboardUrl is not null)
        {
            _lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    _launcher.Open(_dashboardUrl);
                }
                catch (Exception exception)
                {
                    // A shell/browser failure must never stop telemetry or the control plane.
                    _logger.LogWarning(exception, "KAIRON could not open the dashboard automatically at {Url}", _dashboardUrl);
                }
            });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static Uri? ResolveDashboardUrl(IEnumerable<string> arguments, IConfiguration configuration)
    {
        if (!arguments.Contains(DesktopArgument, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var configuredUrl = configuration["Product:DashboardUrl"] ?? "http://127.0.0.1:8000";
        return Uri.TryCreate(configuredUrl, UriKind.Absolute, out var url)
               && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url
            : throw new InvalidOperationException("Product:DashboardUrl must be an absolute HTTP or HTTPS URL.");
    }
}
