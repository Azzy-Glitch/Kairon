using AIDIP.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDIP.Backend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828030000_AddSecureSdkPairing")]
public sealed class AddSecureSdkPairing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlite = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var guid = sqlite ? "TEXT" : "uniqueidentifier";
        var timestamp = sqlite ? "TEXT" : "datetime2";
        string Text(int length) => sqlite ? "TEXT" : $"nvarchar({length})";

        migrationBuilder.CreateTable("SdkPairingSessions", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: false),
            ApplicationId = table.Column<Guid>(type: guid, nullable: false),
            SdkType = table.Column<string>(type: Text(30), maxLength: 30, nullable: false),
            CodeHash = table.Column<string>(type: Text(128), maxLength: 128, nullable: false),
            CreatedAt = table.Column<DateTime>(type: timestamp, nullable: false),
            ExpiresAt = table.Column<DateTime>(type: timestamp, nullable: false),
            RedeemedAt = table.Column<DateTime>(type: timestamp, nullable: true),
            RevokedAt = table.Column<DateTime>(type: timestamp, nullable: true)
        }, constraints: table => table.PrimaryKey("PK_SdkPairingSessions", x => x.Id));

        migrationBuilder.CreateTable("SdkInstallations", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: false),
            ApplicationId = table.Column<Guid>(type: guid, nullable: false),
            SourceId = table.Column<Guid>(type: guid, nullable: false),
            SdkType = table.Column<string>(type: Text(30), maxLength: 30, nullable: false),
            Version = table.Column<string>(type: Text(50), maxLength: 50, nullable: false),
            InstallationId = table.Column<string>(type: Text(200), maxLength: 200, nullable: false),
            KeyPrefix = table.Column<string>(type: Text(16), maxLength: 16, nullable: false),
            KeyHash = table.Column<string>(type: Text(128), maxLength: 128, nullable: false),
            CreatedAt = table.Column<DateTime>(type: timestamp, nullable: false),
            LastSeenAt = table.Column<DateTime>(type: timestamp, nullable: true),
            RevokedAt = table.Column<DateTime>(type: timestamp, nullable: true)
        }, constraints: table => table.PrimaryKey("PK_SdkInstallations", x => x.Id));

        migrationBuilder.CreateIndex("IX_SdkPairingSessions_CodeHash", "SdkPairingSessions", "CodeHash", unique: true);
        migrationBuilder.CreateIndex("IX_SdkPairingSessions_ApplicationId_ExpiresAt", "SdkPairingSessions",
            new[] { "ApplicationId", "ExpiresAt" });
        migrationBuilder.CreateIndex("IX_SdkInstallations_InstallationId", "SdkInstallations", "InstallationId", unique: true);
        migrationBuilder.CreateIndex("IX_SdkInstallations_ProjectId_KeyPrefix", "SdkInstallations",
            new[] { "ProjectId", "KeyPrefix" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("SdkInstallations");
        migrationBuilder.DropTable("SdkPairingSessions");
    }
}
