using AIDIP.Backend.Extensions;
using AIDIP.Backend.Configuration;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Services;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Serilog;

var desktopMode = args.Any(argument => string.Equals(argument, "--desktop", StringComparison.OrdinalIgnoreCase));
var hostArguments = args.Where(argument => !string.Equals(argument, "--desktop", StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // --desktop is a KAIRON product switch, not an ASP.NET configuration key. Remove it so the
    // command-line provider does not consume the following --urls option as its value.
    Args = hostArguments,
    // Installed shortcuts set the working directory, but upgrades, diagnostics, and direct launches
    // are not required to do so. Resolve packaged UI files relative to KAIRON.exe itself.
    ContentRootPath = desktopMode ? AppContext.BaseDirectory : Directory.GetCurrentDirectory()
});

var persistenceOptions = builder.Configuration.GetSection(PersistenceOptions.SectionName).Get<PersistenceOptions>()
                         ?? new PersistenceOptions();
var productPaths = KaironDataPaths.Resolve(persistenceOptions);
productPaths.EnsureCreated();

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(productPaths.Logs, "kairon-.log"), rollingInterval: RollingInterval.Day)
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
builder.Services.AddSwaggerGen();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("telemetry", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0, AutoReplenishment = true }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
builder.Services.AddSingleton<IProductUrlLauncher, ProductUrlLauncher>();
builder.Services.AddHostedService<ProductDashboardLaunchService>();
builder.Services.AddHostedService<LocalAiProcessService>();

// Persistence is infrastructure-configurable: SQLite is Local Mode, SQL Server remains available
// for centralized deployments. Application services only depend on AppDbContext.
builder.Services.AddKaironPersistence(builder.Configuration);

// Application services
builder.Services.AddScoped<IDevOpsService, DevOpsService>();
builder.Services.AddScoped<IContractValidator, ContractValidator>();
builder.Services.AddScoped<IContextEngine, ContextEngine>();
builder.Services.AddScoped<IAgentRegistrationService, AgentRegistrationService>();
builder.Services.AddScoped<IPlatformTelemetryService, PlatformTelemetryService>();
builder.Services.AddScoped<IProjectCredentialService, ProjectCredentialService>();
builder.Services.AddScoped<ISdkPairingService, SdkPairingService>();
builder.Services.Configure<PlatformSecurityOptions>(builder.Configuration.GetSection(PlatformSecurityOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

// AI service + HttpClient
builder.Services.AddAiServices(builder.Configuration);

// Autonomous AI SRE control plane
builder.Services.AddAutonomousSre(builder.Configuration);

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

// KAIRON.exe owns the existing React experience in packaged/local-product mode. API routes are
// mapped below and every other non-file route falls back to the SPA entry point.
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
app.MapFallbackToFile("index.html");

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await scope.ServiceProvider.GetRequiredService<ISqliteBackupService>().CreateAsync("startup-upgrade");
    await dbContext.Database.MigrateAsync();
    if (dbContext.Database.IsSqlite())
    {
        // WAL allows readers and the single backend writer to coexist predictably in Local Mode.
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
    }
}

app.Run();

public partial class Program { }
