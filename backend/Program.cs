using Kairon.Backend.Extensions;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Kairon.Backend.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

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
    .WriteTo.File(Path.Combine(dataPaths.Logs, "kairon-.txt"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add services
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ValidationFilter>();
    // Gates the approve/reject/cancel endpoints when SreSecurity:RequireOperatorKey is enabled.
    options.Filters.Add<OperatorAuthorizationFilter>();
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
    // Scoped to the new normalized telemetry endpoint only (PlatformTelemetryController) - the
    // existing /api/telemetry/* routes have no rate-limit tests today and don't need a new
    // failure mode introduced here.
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
    app.UseHttpsRedirection();
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
        // The existing Migrations/ history was generated against SqlServer only - every column
        // carries an explicit, baked-in SqlServer type string (e.g. "nvarchar(max)"), which
        // Migrate() would send to SQLite verbatim and fail on ("near 'max': syntax error" -
        // confirmed live, not theoretical). EnsureCreated() instead builds the schema directly
        // from the current model with SQLite's own type mappings, sidestepping the migrations
        // history entirely. This is safe for every SQLite install that exists today, because
        // SQLite has only just become the real default - there is no installed base yet whose
        // existing database this could silently fail to upgrade. EnsureCreated() is also a safe
        // no-op against an already-created database (restart persistence keeps working). Before
        // the next schema change ships to real SQLite users, this needs a genuine SQLite-specific
        // migration history (EF Core requires a separate migrations assembly per provider once
        // column types diverge like this) - tracked, not swept under the rug.
        await dbContext.Database.EnsureCreatedAsync();

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
}

app.Run();

public partial class Program { }
