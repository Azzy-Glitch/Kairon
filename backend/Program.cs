using AIDIP.Backend.Extensions;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/aidip-.txt", rollingInterval: RollingInterval.Day)
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
builder.Services.AddSingleton<IProductUrlLauncher, ProductUrlLauncher>();
builder.Services.AddHostedService<ProductDashboardLaunchService>();

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
    await dbContext.Database.MigrateAsync();
}

app.Run();

public partial class Program { }
