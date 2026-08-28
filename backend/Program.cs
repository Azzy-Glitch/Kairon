using Kairon.Backend.Extensions;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/kairon-.txt", rollingInterval: RollingInterval.Day)
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

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// Application services
builder.Services.AddScoped<IDevOpsService, DevOpsService>();
builder.Services.AddScoped<IContractValidator, ContractValidator>();
builder.Services.AddScoped<IContextEngine, ContextEngine>();

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
    await dbContext.Database.MigrateAsync();
}

app.Run();

public partial class Program { }
