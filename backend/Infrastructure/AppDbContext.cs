using Kairon.Backend.Models;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Models.Sre;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // Existing sets - unchanged.
    public DbSet<Incident> Incidents { get; set; }
    public DbSet<Metric> Metrics { get; set; }
    public DbSet<Analysis> Analyses { get; set; }

    // KAIRON Agent events - log/process signals (docs/OBSERVABILITY_MIGRATION.md).
    public DbSet<AgentEvent> AgentEvents { get; set; }

    // Autonomous SRE sets (PRD section 16: additive, reusing the existing telemetry entities).
    public DbSet<SreIncident> SreIncidents { get; set; }
    public DbSet<IncidentEvent> IncidentEvents { get; set; }
    public DbSet<IncidentEvidence> IncidentEvidence { get; set; }
    public DbSet<RemediationAction> RemediationActions { get; set; }
    public DbSet<VerificationResult> VerificationResults { get; set; }

    // Platform: projects, SDK pairing/credentials, platform-level audit (docs/DESKTOP_SHELL.md).
    public DbSet<Project> Projects { get; set; }
    public DbSet<ProjectApiCredential> ProjectApiCredentials { get; set; }
    public DbSet<SdkPairingSession> SdkPairingSessions { get; set; }
    public DbSet<PlatformAuditEvent> PlatformAuditEvents { get; set; }

    // User-configured AI provider/model/key (frontend AI Configuration panel) - additive, replaces
    // no existing entity; env/appsettings-based AI configuration is untouched and still works.
    public DbSet<AiProviderConfig> AiProviderConfigs { get; set; }

    // Machine/process discovery - "Basic Monitoring" (docs/DESKTOP_SHELL.md).
    public DbSet<Machine> Machines { get; set; }
    public DbSet<DiscoveredApplication> DiscoveredApplications { get; set; }

    // Normalized telemetry pipeline + SDK installation identity (docs/DESKTOP_SHELL.md) - additive
    // on top of the existing Project entity, not a replacement of it.
    public DbSet<MonitoredApplication> MonitoredApplications { get; set; }
    public DbSet<KaironEnvironment> Environments { get; set; }
    public DbSet<TelemetrySourceRegistration> TelemetrySources { get; set; }
    public DbSet<SdkInstallation> SdkInstallations { get; set; }
    public DbSet<TelemetryReceipt> TelemetryReceipts { get; set; }

    // Database-backed replacement for WindowsRemediation:Targets (Configuration/
    // WindowsRemediationOptions.cs) - see Models/Platform/RemediationTarget.cs.
    public DbSet<RemediationTarget> RemediationTargets { get; set; }

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

        modelBuilder.Entity<AgentEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Timestamp });
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.Environment);
            entity.Property(e => e.EventType).HasMaxLength(60).IsRequired();
            entity.Property(e => e.Environment).HasMaxLength(100);
            entity.Property(e => e.Application).HasMaxLength(200);
            entity.Property(e => e.Service).HasMaxLength(200);
            entity.Property(e => e.Component).HasMaxLength(200);
            entity.Property(e => e.Severity).HasMaxLength(20);
            entity.Property(e => e.Message).HasMaxLength(4000);
            entity.Property(e => e.Source).HasMaxLength(500);
            entity.Property(e => e.MetadataJson).HasMaxLength(4000);
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

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Slug).HasMaxLength(200);

            entity.HasMany<ProjectApiCredential>()
                  .WithOne()
                  .HasForeignKey(e => e.ProjectId)
                  .OnDelete(DeleteBehavior.Cascade);

            // A remediation target has no meaning without its project - removing a project
            // removes its targets too, matching the ProjectApiCredential cascade above.
            entity.HasMany<RemediationTarget>()
                  .WithOne()
                  .HasForeignKey(e => e.ProjectId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiProviderConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(200);
            entity.Property(e => e.Endpoint).HasMaxLength(500);
            entity.Property(e => e.EncryptedApiKey).HasMaxLength(4000);
        });

        modelBuilder.Entity<MonitoredApplication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Service }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Service).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Runtime).HasMaxLength(50);
        });

        modelBuilder.Entity<KaironEnvironment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<TelemetrySourceRegistration>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.InstallationId }).IsUnique();
            entity.Property(e => e.SourceType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.InstallationId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Version).HasMaxLength(50);
        });

        modelBuilder.Entity<SdkInstallation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.KeyPrefix });
            entity.Property(e => e.SdkType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Version).HasMaxLength(50);
            entity.Property(e => e.InstallationId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.KeyPrefix).HasMaxLength(20).IsRequired();
            entity.Property(e => e.KeyHash).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<TelemetryReceipt>(entity =>
        {
            entity.HasKey(e => e.Id);
            // EventId is the idempotency key - a resend is looked up by this exactly once per
            // ingest, and the unique index is what makes "already accepted" a database-level
            // guarantee rather than an application-level race.
            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => new { e.ProjectId, e.EventTimestamp });
            entity.Property(e => e.EventType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Severity).HasMaxLength(20);
            entity.Property(e => e.Source).HasMaxLength(50);
            entity.Property(e => e.Application).HasMaxLength(200);
            entity.Property(e => e.Service).HasMaxLength(200);
            entity.Property(e => e.Environment).HasMaxLength(100);
            entity.Property(e => e.PayloadJson).HasMaxLength(16000);
        });

        modelBuilder.Entity<ProjectApiCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.KeyPrefix });
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.KeyPrefix).HasMaxLength(20).IsRequired();
            entity.Property(e => e.KeyHash).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<RemediationTarget>(entity =>
        {
            entity.HasKey(e => e.Id);
            // Runtime resolution (WindowsServiceTool/DetectionEngine/TelemetryController) only
            // ever matches Enabled targets, so only enabled targets need a unique logical
            // identity - a disabled target can coexist with its enabled replacement without
            // being deleted. SQLite supports a WHERE-filtered unique index natively; the
            // SQL-Server-flavored EF migration generates the equivalent filtered index for that
            // provider. See SqliteSchemaMigrator for the hand-written SQLite DDL this maps to.
            entity.HasIndex(e => new { e.ProjectId, e.Environment, e.Service })
                  .IsUnique()
                  .HasFilter("Enabled = 1");
            // Machine and TelemetryCredentialId are loose Guid references (indexed, not a real
            // FK) - matching this codebase's dominant convention for cross-entity references
            // that are validated at the application layer (active/revoked/heartbeat checks)
            // rather than enforced by referential integrity (Incident.MachineId, Metric.MachineId
            // follow the same pattern).
            entity.HasIndex(e => e.MachineId);
            entity.HasIndex(e => e.TelemetryCredentialId);
            entity.Property(e => e.Environment).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Service).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ExpectedHostName).HasMaxLength(255).IsRequired();
            entity.Property(e => e.WindowsServiceName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AllowedOperationsJson).HasMaxLength(500).IsRequired();
            // A genuine, database-enforced optimistic-concurrency check: EF includes this
            // property's originally-read value in every UPDATE's WHERE clause and throws
            // DbUpdateConcurrencyException when zero rows match (RemediationTargetManagementService
            // translates that into a 409 "stale-update"). Purely a mapping-level annotation - no
            // schema/column change, so no migration is required for either provider. This is in
            // addition to, not instead of, the explicit ExpectedUpdatedAt pre-check: that check
            // catches the common "operator edited a stale form" case with a clear message even
            // when nothing is truly racing; this token is what makes two genuinely simultaneous
            // requests resolve to exactly one winner rather than a silent lost update.
            entity.Property(e => e.UpdatedAt).IsConcurrencyToken();
        });

        modelBuilder.Entity<SdkPairingSession>(entity =>
        {
            entity.HasKey(e => e.Id);
            // A pairing code is looked up by its hash exactly once, at redemption - uniqueness
            // here is what makes "already used" a real database-level guarantee, not just an
            // application-level check that a race could slip past.
            entity.HasIndex(e => e.CodeHash).IsUnique();
            entity.Property(e => e.SdkType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.CodeHash).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<PlatformAuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.Timestamp });
            entity.HasIndex(e => e.Action);
            entity.Property(e => e.Action).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(200).IsRequired();
            entity.Property(e => e.TargetType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.TargetId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Result).HasMaxLength(40).IsRequired();
            entity.Property(e => e.Message).HasMaxLength(2000);
            entity.Property(e => e.DataJson).HasMaxLength(8000);
        });

        modelBuilder.Entity<Machine>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.HostName).HasMaxLength(255).IsRequired();
            entity.Property(e => e.OperatingSystem).HasMaxLength(500);
            entity.Property(e => e.Architecture).HasMaxLength(50);
            entity.Property(e => e.AgentVersion).HasMaxLength(50);
            entity.Property(e => e.AgentCredentialHash).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<DiscoveredApplication>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MachineId);
            // Includes Source: the same physical OS process can legitimately be observed and
            // reported by BOTH components (e.g. the Windows Service's one watched process happens
            // to also fall in the UserAgent's own session) - each source gets its own row for it,
            // never a unique-key collision between them.
            entity.HasIndex(e => new { e.MachineId, e.ProcessId, e.ProcessStartedAt, e.Source }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Executable).HasMaxLength(2000);
            entity.Property(e => e.Runtime).HasMaxLength(50);
            entity.Property(e => e.Source).HasMaxLength(20).IsRequired().HasDefaultValue("MachineAgent");
            entity.Property(e => e.UserName).HasMaxLength(300);
        });
    }
}
