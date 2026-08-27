using AIDIP.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDIP.Backend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828003000_AddPlatformIdentityAndNormalizedTelemetry")]
public sealed class AddPlatformIdentityAndNormalizedTelemetry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlite = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var guid = sqlite ? "TEXT" : "uniqueidentifier";
        var text20 = sqlite ? "TEXT" : "nvarchar(20)";
        var text50 = sqlite ? "TEXT" : "nvarchar(50)";
        var text100 = sqlite ? "TEXT" : "nvarchar(100)";
        var text200 = sqlite ? "TEXT" : "nvarchar(200)";
        var text16000 = sqlite ? "TEXT" : "nvarchar(max)";
        var timestamp = sqlite ? "TEXT" : "datetime2";
        var boolean = sqlite ? "INTEGER" : "bit";

        migrationBuilder.CreateTable("Projects", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            Name = table.Column<string>(type: text200, maxLength: 200, nullable: false),
            Slug = table.Column<string>(type: text100, maxLength: 100, nullable: false),
            IsActive = table.Column<bool>(type: boolean, nullable: false),
            CreatedAt = table.Column<DateTime>(type: timestamp, nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Projects", x => x.Id));

        migrationBuilder.CreateTable("TelemetryReceipts", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            EventId = table.Column<Guid>(type: guid, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: false),
            SourceId = table.Column<Guid>(type: guid, nullable: true),
            EventTimestamp = table.Column<DateTime>(type: timestamp, nullable: false),
            EventType = table.Column<string>(type: text50, maxLength: 50, nullable: false),
            Severity = table.Column<string>(type: text20, maxLength: 20, nullable: false),
            Source = table.Column<string>(type: text50, maxLength: 50, nullable: false),
            Application = table.Column<string>(type: text200, maxLength: 200, nullable: false),
            Service = table.Column<string>(type: text200, maxLength: 200, nullable: false),
            Environment = table.Column<string>(type: text100, maxLength: 100, nullable: false),
            PayloadJson = table.Column<string>(type: text16000, maxLength: 16000, nullable: false),
            ReceivedAt = table.Column<DateTime>(type: timestamp, nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TelemetryReceipts", x => x.Id));

        migrationBuilder.CreateTable("ProjectApiCredentials", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: false),
            Name = table.Column<string>(type: text100, maxLength: 100, nullable: false),
            KeyPrefix = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(16)", maxLength: 16, nullable: false),
            KeyHash = table.Column<string>(type: sqlite ? "TEXT" : "nvarchar(128)", maxLength: 128, nullable: false),
            CreatedAt = table.Column<DateTime>(type: timestamp, nullable: false),
            RevokedAt = table.Column<DateTime>(type: timestamp, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ProjectApiCredentials", x => x.Id);
            table.ForeignKey("FK_ProjectApiCredentials_Projects_ProjectId", x => x.ProjectId,
                "Projects", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable("Environments", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: false),
            Name = table.Column<string>(type: text100, maxLength: 100, nullable: false),
            CreatedAt = table.Column<DateTime>(type: timestamp, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Environments", x => x.Id);
            table.ForeignKey("FK_Environments_Projects_ProjectId", x => x.ProjectId, "Projects", "Id",
                onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable("MonitoredApplications", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: false),
            Name = table.Column<string>(type: text200, maxLength: 200, nullable: false),
            Service = table.Column<string>(type: text200, maxLength: 200, nullable: false),
            Runtime = table.Column<string>(type: text50, maxLength: 50, nullable: false),
            CreatedAt = table.Column<DateTime>(type: timestamp, nullable: false),
            LastTelemetryAt = table.Column<DateTime>(type: timestamp, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_MonitoredApplications", x => x.Id);
            table.ForeignKey("FK_MonitoredApplications_Projects_ProjectId", x => x.ProjectId, "Projects", "Id",
                onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable("TelemetrySources", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: false),
            ApplicationId = table.Column<Guid>(type: guid, nullable: true),
            SourceType = table.Column<string>(type: text50, maxLength: 50, nullable: false),
            InstallationId = table.Column<string>(type: text200, maxLength: 200, nullable: false),
            Version = table.Column<string>(type: text50, maxLength: 50, nullable: false),
            RegisteredAt = table.Column<DateTime>(type: timestamp, nullable: false),
            LastSeenAt = table.Column<DateTime>(type: timestamp, nullable: false),
            IsActive = table.Column<bool>(type: boolean, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_TelemetrySources", x => x.Id);
            table.ForeignKey("FK_TelemetrySources_MonitoredApplications_ApplicationId", x => x.ApplicationId,
                "MonitoredApplications", "Id", onDelete: ReferentialAction.SetNull);
        });

        migrationBuilder.CreateIndex("IX_Projects_Slug", "Projects", "Slug", unique: true);
        migrationBuilder.CreateIndex("IX_ProjectApiCredentials_ProjectId_KeyPrefix", "ProjectApiCredentials",
            new[] { "ProjectId", "KeyPrefix" });
        migrationBuilder.CreateIndex("IX_Environments_ProjectId_Name", "Environments", new[] { "ProjectId", "Name" }, unique: true);
        migrationBuilder.CreateIndex("IX_MonitoredApplications_ProjectId_Service", "MonitoredApplications", new[] { "ProjectId", "Service" }, unique: true);
        migrationBuilder.CreateIndex("IX_TelemetrySources_ApplicationId", "TelemetrySources", "ApplicationId");
        migrationBuilder.CreateIndex("IX_TelemetrySources_LastSeenAt", "TelemetrySources", "LastSeenAt");
        migrationBuilder.CreateIndex("IX_TelemetrySources_ProjectId_InstallationId", "TelemetrySources", new[] { "ProjectId", "InstallationId" }, unique: true);
        migrationBuilder.CreateIndex("IX_TelemetryReceipts_EventId", "TelemetryReceipts", "EventId", unique: true);
        migrationBuilder.CreateIndex("IX_TelemetryReceipts_ProjectId_ReceivedAt", "TelemetryReceipts", new[] { "ProjectId", "ReceivedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Environments");
        migrationBuilder.DropTable("TelemetryReceipts");
        migrationBuilder.DropTable("TelemetrySources");
        migrationBuilder.DropTable("ProjectApiCredentials");
        migrationBuilder.DropTable("MonitoredApplications");
        migrationBuilder.DropTable("Projects");
    }
}
