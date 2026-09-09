using Kairon.Backend.Extensions;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
if (!ProductEnvironments.Contains(builder.Environment.EnvironmentName))
    throw new InvalidOperationException("KAIRON runtime environment must be Development, Staging or Production.");
if (builder.Environment.IsProduction() &&
    (!builder.Configuration.GetValue("PlatformSecurity:RequireTelemetryKey", true) ||
     !builder.Configuration.GetValue("SreSecurity:RequireOperatorKey", true)))
    throw new InvalidOperationException("Production requires telemetry and operator authentication.");

// Resolve the same writable runtime-data layout used by SQLite before Serilog opens its file
// sink. The installed desktop backend runs as the interactive user and cannot write beneath
// Program Files; %LOCALAPPDATA%\Kairon\logs (or the explicit database path's sibling logs folder)
// remains writable without weakening binary-directory ACLs.
var persistenceOptions = builder.Configuration
    .GetSection(PersistenceOptions.SectionName)
    .Get<PersistenceOptions>() ?? new PersistenceOptions();
var dataPaths = KaironDataPaths.Resolve(persistenceOptions);
dataPaths.EnsureLogsCreated();

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(dataPaths.Logs, "kairon-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true)
    .CreateLogger();

builder.Host.UseSerilog();

// Data Protection - encrypts the AI Configuration panel's stored API key at rest (Windows DPAPI
// backs the key ring by default). Keyed to the same product data directory as everything else, so
// an uninstall/reinstall or a fresh machine profile doesn't leave keys orphaned in a generic
// per-user ASP.NET location.
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("Kairon")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPaths.Config, "dataprotection-keys")));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

// Database selection changes are applied only before constructing the persistence service graph.
await DatabaseConfigurationService.ApplySavedAtStartupAsync(builder.Configuration, dataPaths, builder.Environment.IsProduction());
var selectedDatabaseProvider = builder.Configuration.GetValue<string>("Persistence:Provider") ?? "SQLite";
var sqliteSelected = selectedDatabaseProvider.Equals("SQLite", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(new DatabaseConfigurationLocation(Path.Combine(dataPaths.Config, "database-settings.protected")));
builder.Services.AddSingleton(new DatabaseRuntimeSelection(sqliteSelected ? "SQLite" : "SqlServer",
    sqliteSelected ? "SQLite" : DatabaseConfigurationService.ConnectionSignature(builder.Configuration.GetConnectionString("DefaultConnection") ?? "")));
builder.Services.AddSingleton<IDatabaseConnectionProbe, SqlServerConnectionProbe>();
builder.Services.AddSingleton<DatabaseConfigurationService>();

// Add services
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ValidationFilter>();
    // Gates the approve/reject/cancel endpoints when SreSecurity:RequireOperatorKey is enabled.
    options.Filters.Add<OperatorAuthorizationFilter>();
}).AddJsonOptions(options =>
{
    // SQLite returns stored UTC DateTimes as Kind=Unspecified. Always include the UTC marker so
    // browsers convert observations to the operator's actual local time instead of showing UTC
    // clock values as if they were already local.
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Kairon Backend",
        Version = "v1",
        Description = "Kairon — Autonomous AI SRE backend API: detection, correlation, AI investigation and remediation."
    });
});

// Database - SQLite (default, packaged desktop product) or SqlServer (centralized/cloud
// deployments) via Persistence:Provider. See backend/Infrastructure/PersistenceRegistration.cs.
builder.Services.AddKaironPersistence(builder.Configuration);
builder.Services.Configure<PersistenceOptions>(
    builder.Configuration.GetSection(PersistenceOptions.SectionName));
builder.Services.AddPersistenceMaintenance();

builder.Services.AddRateLimiter(options =>
{
    // Shared by normalized and legacy ingestion. It bounds local abuse before work can reach
    // persistence/detection while keeping SDK sends non-blocking (rejected work receives 429).
    options.AddPolicy("telemetry", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Application services
builder.Services.AddScoped<IDevOpsService, DevOpsService>();
builder.Services.AddScoped<IContractValidator, ContractValidator>();
builder.Services.AddScoped<IContextEngine, ContextEngine>();

// AI service + HttpClient
builder.Services.AddAiServices(builder.Configuration);

// Autonomous AI SRE control plane
builder.Services.AddAutonomousSre(builder.Configuration);

// Projects, SDK pairing/credentials, platform audit
builder.Services.AddPlatformServices(builder.Configuration);

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("Development", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });

    options.AddPolicy("Production", policy =>
    {
        var allowedOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();

        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseCors("Development");
}
else
{
    app.UseCors("Production");
    // No app.UseHttpsRedirection() here, deliberately: Kairon only ever binds Kestrel to a plain
    // http://127.0.0.1 loopback endpoint (MainForm launches it with --urls http://127.0.0.1:8000)
    // - there is no HTTPS endpoint configured anywhere in this product for it to redirect to. With
    // the middleware registered anyway, ASP.NET Core logged "Failed to determine the https port
    // for redirect" on every request; since there genuinely is no https port and never will be for
    // a local-loopback desktop backend, the fix is removing the middleware, not silencing the log.
}

app.UseSerilogRequestLogging();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseRateLimiter();

// Serves the React production build from wwwroot (populated by `npm run build` + a copy step -
// see docs/DESKTOP_SHELL.md) so the desktop shell and any browser can load the UI directly from
// this API process instead of requiring a separate Vite dev server. Registered before the health/
// API routes only affects static asset matching; MapFallbackToFile below is what runs last.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/api/health");

app.MapHealthChecks(
    "/api/health/ready",
    new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => true
    });

app.MapControllers();

// SPA fallback - only for GET requests that matched no API route or static file, so it can never
// shadow /api/*. Missing wwwroot/index.html (no frontend built yet) is a no-op, not an error.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html")))
{
    app.MapFallbackToFile("index.html");
}

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // A consistent SQLite backup before every startup migration - a failed/aborted migration on
    // an upgrade never has to mean lost data. No-op under SqlServer (see SqliteBackupService).
    await scope.ServiceProvider.GetRequiredService<ISqliteBackupService>().CreateAsync("startup-upgrade");

    if (dbContext.Database.IsSqlite())
    {
        await scope.ServiceProvider.GetRequiredService<ILocalSchemaMigrator>().MigrateAsync();

        // WAL lets the single backend writer and any concurrent readers coexist predictably in
        // the packaged desktop product; foreign_keys/busy_timeout are SQLite session settings,
        // not persisted in the file, so they're applied here on every startup.
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
    }
    else
    {
        await dbContext.Database.MigrateAsync();
    }

    // One-time, idempotent import of WindowsRemediation:Targets (appsettings.json) into the
    // database-backed RemediationTarget table - see RemediationTargetResolver's remarks for the
    // compatibility strategy. A no-op on every startup after the first successful import, and a
    // no-op entirely when no legacy targets are configured (true of a fresh install today).
    await scope.ServiceProvider.GetRequiredService<IRemediationTargetResolver>().ImportLegacyConfigurationAsync();
}

app.Run();

public partial class Program { }
