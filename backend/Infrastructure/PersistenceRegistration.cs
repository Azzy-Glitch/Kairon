using AIDIP.Backend.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AIDIP.Backend.Infrastructure;

public static class PersistenceRegistration
{
    public static IServiceCollection AddKaironPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var persistence = configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>()
                          ?? new PersistenceOptions();
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.AddSingleton<PersistenceMaintenanceState>();
        services.AddSingleton<ISqliteBackupService, SqliteBackupService>();
        services.AddHostedService<PersistenceMaintenanceService>();

        if (persistence.Provider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
        {
            var paths = KaironDataPaths.Resolve(persistence);
            paths.EnsureCreated();
            var databasePath = string.IsNullOrWhiteSpace(persistence.DatabasePath)
                ? paths.DatabasePath
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(persistence.DatabasePath));
            services.AddSingleton(paths);
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={databasePath};Cache=Shared;Pooling=True")
                    // The preserved historical snapshot carries SQL Server store-type annotations.
                    // Provider-aware migrations are reviewed/tested explicitly for SQLite instead.
                    .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
            return services;
        }

        if (persistence.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var connection = configuration.GetConnectionString("DefaultConnection")
                             ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required for SqlServer mode.");
            services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connection));
            return services;
        }

        throw new InvalidOperationException($"Unsupported persistence provider '{persistence.Provider}'. Use SQLite or SqlServer.");
    }
}
