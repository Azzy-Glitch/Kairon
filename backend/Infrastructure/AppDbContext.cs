using AIDIP.Backend.Models;
using AIDIP.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // Existing sets - unchanged.
    public DbSet<Incident> Incidents { get; set; }
    public DbSet<Metric> Metrics { get; set; }
    public DbSet<Analysis> Analyses { get; set; }
    public DbSet<Machine> Machines { get; set; }
    public DbSet<DiscoveredApplication> DiscoveredApplications { get; set; }
    public DbSet<KaironProject> Projects { get; set; }
    public DbSet<MonitoredApplication> MonitoredApplications { get; set; }
    public DbSet<KaironEnvironment> Environments { get; set; }
    public DbSet<TelemetrySourceRegistration> TelemetrySources { get; set; }
    public DbSet<TelemetryReceipt> TelemetryReceipts { get; set; }
    public DbSet<ProjectApiCredential> ProjectApiCredentials { get; set; }

    // Autonomous SRE sets (PRD section 16: additive, reusing the existing telemetry entities).
    public DbSet<SreIncident> SreIncidents { get; set; }
    public DbSet<IncidentEvent> IncidentEvents { get; set; }
    public DbSet<IncidentEvidence> IncidentEvidence { get; set; }
    public DbSet<RemediationAction> RemediationActions { get; set; }
    public DbSet<VerificationResult> VerificationResults { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Incident>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Timestamp });
            entity.HasIndex(e => e.Endpoint);
            entity.HasIndex(e => e.StatusCode);
            entity.HasIndex(e => e.Environment);
            entity.HasIndex(e => e.SreIncidentId);
            entity.Property(e => e.ErrorMessage).HasMaxLength(4000);
            entity.Property(e => e.StackTrace).HasMaxLength(8000);
            entity.Property(e => e.Application).HasMaxLength(200);
            entity.Property(e => e.Service).HasMaxLength(200);
        });

        modelBuilder.Entity<Metric>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Timestamp });
            entity.HasIndex(e => e.Environment);
            entity.Property(e => e.Application).HasMaxLength(200);
            entity.Property(e => e.Service).HasMaxLength(200);
            entity.Property(e => e.Component).HasMaxLength(200);
        });

        modelBuilder.Entity<Analysis>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Type });
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.OutputJson).HasMaxLength(16000);
        });

        modelBuilder.Entity<Machine>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.HostName).HasMaxLength(255).IsRequired();
            entity.Property(e => e.OperatingSystem).HasMaxLength(500);
            entity.Property(e => e.Architecture).HasMaxLength(50);
            entity.Property(e => e.AgentVersion).HasMaxLength(50);
            entity.Property(e => e.AgentCredentialHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(e => e.LastSeenAt);
        });

        modelBuilder.Entity<DiscoveredApplication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.MachineId, e.ProcessId, e.ProcessStartedAt }).IsUnique();
            entity.HasIndex(e => new { e.MachineId, e.IsRunning });
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Executable).HasMaxLength(2000);
            entity.Property(e => e.Runtime).HasMaxLength(50);
            entity.HasOne(e => e.Machine).WithMany(e => e.Applications).HasForeignKey(e => e.MachineId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KaironProject>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Slug).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Slug).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<MonitoredApplication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Service }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Service).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Runtime).HasMaxLength(50);
            entity.HasOne(e => e.Project).WithMany(e => e.Applications).HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KaironEnvironment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.HasOne(e => e.Project).WithMany(e => e.Environments).HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TelemetrySourceRegistration>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.InstallationId }).IsUnique();
            entity.HasIndex(e => e.LastSeenAt);
            entity.Property(e => e.SourceType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.InstallationId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Version).HasMaxLength(50);
            entity.HasOne(e => e.Application).WithMany(e => e.TelemetrySources).HasForeignKey(e => e.ApplicationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TelemetryReceipt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.ReceivedAt });
            entity.Property(e => e.EventType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Severity).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Application).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Service).HasMaxLength(200);
            entity.Property(e => e.Environment).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PayloadJson).HasMaxLength(16000).IsRequired();
        });

        modelBuilder.Entity<ProjectApiCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.KeyPrefix });
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.KeyPrefix).HasMaxLength(16).IsRequired();
            entity.Property(e => e.KeyHash).HasMaxLength(128).IsRequired();
            entity.HasOne(e => e.Project).WithMany(e => e.Credentials).HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SreIncident>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IncidentKey).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.Timestamp });
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CorrelationKey);
            entity.HasIndex(e => new { e.Environment, e.Service });

            entity.Property(e => e.IncidentKey).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Application).HasMaxLength(200);
            entity.Property(e => e.Service).HasMaxLength(200);
            entity.Property(e => e.Environment).HasMaxLength(100);
            entity.Property(e => e.AffectedComponent).HasMaxLength(300);
            entity.Property(e => e.AffectedEndpoint).HasMaxLength(500);
            entity.Property(e => e.Title).HasMaxLength(400);
            entity.Property(e => e.CorrelationKey).HasMaxLength(400);
            entity.Property(e => e.RootCause).HasMaxLength(4000);
            entity.Property(e => e.Summary).HasMaxLength(4000);
            entity.Property(e => e.PredictedImpact).HasMaxLength(4000);
            entity.Property(e => e.PredictedRisk).HasMaxLength(50);
            entity.Property(e => e.FailureReason).HasMaxLength(2000);
            entity.Property(e => e.SymptomsJson).HasMaxLength(8000);
            entity.Property(e => e.TelemetryReferencesJson).HasMaxLength(8000);
            entity.Property(e => e.CorrelatedMetricsJson).HasMaxLength(16000);
            entity.Property(e => e.ContributingFactorsJson).HasMaxLength(8000);
            entity.Property(e => e.RecommendationsJson).HasMaxLength(16000);

            // Enums are stored as strings: an incident's status is read by humans in the database
            // as often as by the application, and a renumbered enum must never silently reinterpret
            // existing rows.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.RemediationState).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.VerificationState).HasConversion<string>().HasMaxLength(40);

            entity.HasMany(e => e.Events)
                  .WithOne(e => e.Incident!)
                  .HasForeignKey(e => e.IncidentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Evidence)
                  .WithOne(e => e.Incident!)
                  .HasForeignKey(e => e.IncidentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Actions)
                  .WithOne(e => e.Incident!)
                  .HasForeignKey(e => e.IncidentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Verifications)
                  .WithOne(e => e.Incident!)
                  .HasForeignKey(e => e.IncidentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IncidentEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.IncidentId, e.Timestamp });
            entity.Property(e => e.EventType).HasMaxLength(60).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(200);
            entity.Property(e => e.PreviousState).HasMaxLength(40);
            entity.Property(e => e.NewState).HasMaxLength(40);
            entity.Property(e => e.ActionId).HasMaxLength(32);
            entity.Property(e => e.Result).HasMaxLength(200);
            entity.Property(e => e.Message).HasMaxLength(4000);
            entity.Property(e => e.Error).HasMaxLength(4000);
            entity.Property(e => e.DataJson).HasMaxLength(8000);
        });

        modelBuilder.Entity<IncidentEvidence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.IncidentId, e.Kind });
            entity.Property(e => e.Kind).HasMaxLength(60).IsRequired();
            entity.Property(e => e.Summary).HasMaxLength(1000);
            entity.Property(e => e.PayloadJson).HasMaxLength(16000);
        });

        modelBuilder.Entity<RemediationAction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ActionKey).IsUnique();
            entity.HasIndex(e => new { e.IncidentId, e.Status });
            entity.Property(e => e.ActionKey).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ActionType).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(2000);
            entity.Property(e => e.ExpectedOutcome).HasMaxLength(2000);
            entity.Property(e => e.ParametersJson).HasMaxLength(4000);
            entity.Property(e => e.Source).HasMaxLength(50);
            entity.Property(e => e.PolicyDecision).HasMaxLength(1000);
            entity.Property(e => e.ApprovedBy).HasMaxLength(200);
            entity.Property(e => e.RejectedBy).HasMaxLength(200);
            entity.Property(e => e.RejectionReason).HasMaxLength(2000);
            entity.Property(e => e.ExecutionResult).HasMaxLength(4000);
            entity.Property(e => e.ExecutionError).HasMaxLength(4000);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.RiskLevel).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<VerificationResult>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.IncidentId, e.StartedAt });
            entity.Property(e => e.Summary).HasMaxLength(2000);
            entity.Property(e => e.ComparisonsJson).HasMaxLength(8000);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(40);
        });
    }
}
