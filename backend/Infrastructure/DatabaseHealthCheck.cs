using Microsoft.Extensions.Diagnostics.HealthChecks;

using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Infrastructure;

public class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(AppDbContext dbContext, ILogger<DatabaseHealthCheck> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
            if (!canConnect) return HealthCheckResult.Unhealthy("Database connection failed");
            if (_dbContext.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
            {
                await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA quick_check;";
                if (command.Connection!.State != System.Data.ConnectionState.Open)
                    await command.Connection.OpenAsync(cancellationToken);
                var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    return HealthCheckResult.Unhealthy("SQLite integrity check failed");
            }
            return HealthCheckResult.Healthy("Database connection and integrity are healthy");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database connection failed", ex);
        }
    }
}
