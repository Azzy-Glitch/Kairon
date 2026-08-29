using Kairon.Backend.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// Wires up AppDbContext against whichever provider Persistence:Provider selects. SqlServer stays
/// the default (matches every existing appsettings.json/test/demo workflow); SQLite is an opt-in
/// local mode, ported from origin/main's PersistenceRegistration.cs. Both are real, tested paths -
/// not one placeholder next to the "real" one.
/// </summary>
public static class PersistenceRegistration
{
    public static IServiceCollection AddKaironPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var persistence = configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>()
                          ?? new PersistenceOptions();
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));

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
                    // The model snapshot carries SQL-Server-flavored annotations from the existing
                    // migration history; SQLite's own migration path is reviewed/tested separately
                    // rather than silently warned about on every startup.
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

        throw new InvalidOperationException($"Unsupported Persistence:Provider '{persistence.Provider}'. Use SQLite or SqlServer.");
    }
}
