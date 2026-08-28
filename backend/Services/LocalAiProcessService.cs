using System.Diagnostics;

namespace AIDIP.Backend.Services;

/// <summary>Owns the packaged local AI process; cloud/server deployments simply have no adjacent executable.</summary>
public sealed class LocalAiProcessService : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalAiProcessService> _logger;
    private Process? _process;

    public LocalAiProcessService(IConfiguration configuration, ILogger<LocalAiProcessService> logger)
    { _configuration = configuration; _logger = logger; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Product:ManageLocalAi", true)) return Task.CompletedTask;
        var executable = Path.Combine(AppContext.BaseDirectory, "ai", "KAIRON.AI.exe");
        if (!File.Exists(executable)) return Task.CompletedTask;
        try
        {
            _process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false,
                CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
            _logger.LogInformation("Started packaged KAIRON AI gateway process");
        }
        catch (Exception exception) { _logger.LogWarning(exception, "Could not start packaged KAIRON AI gateway"); }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited) return;
        try { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(cancellationToken); }
        catch (Exception exception) { _logger.LogWarning(exception, "Could not stop packaged KAIRON AI gateway cleanly"); }
    }

    public void Dispose() => _process?.Dispose();
}
