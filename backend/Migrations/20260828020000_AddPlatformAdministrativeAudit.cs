using AIDIP.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDIP.Backend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828020000_AddPlatformAdministrativeAudit")]
public sealed class AddPlatformAdministrativeAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlite = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var guid = sqlite ? "TEXT" : "uniqueidentifier";
        var timestamp = sqlite ? "TEXT" : "datetime2";
        string Text(int length) => sqlite ? "TEXT" : $"nvarchar({length})";
        var longText = sqlite ? "TEXT" : "nvarchar(max)";

        migrationBuilder.CreateTable("PlatformAuditEvents", table => new
        {
            Id = table.Column<Guid>(type: guid, nullable: false),
            Timestamp = table.Column<DateTime>(type: timestamp, nullable: false),
            Category = table.Column<string>(type: Text(50), maxLength: 50, nullable: false),
            Action = table.Column<string>(type: Text(100), maxLength: 100, nullable: false),
            Actor = table.Column<string>(type: Text(200), maxLength: 200, nullable: false),
            TargetType = table.Column<string>(type: Text(100), maxLength: 100, nullable: false),
            TargetId = table.Column<string>(type: Text(200), maxLength: 200, nullable: false),
            ProjectId = table.Column<Guid>(type: guid, nullable: true),
            Result = table.Column<string>(type: Text(40), maxLength: 40, nullable: false),
            Message = table.Column<string>(type: Text(2000), maxLength: 2000, nullable: true),
            DataJson = table.Column<string>(type: longText, maxLength: 8000, nullable: true)
        }, constraints: table => table.PrimaryKey("PK_PlatformAuditEvents", x => x.Id));

        migrationBuilder.CreateIndex("IX_PlatformAuditEvents_Timestamp", "PlatformAuditEvents", "Timestamp");
        migrationBuilder.CreateIndex("IX_PlatformAuditEvents_ProjectId_Timestamp", "PlatformAuditEvents",
            new[] { "ProjectId", "Timestamp" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("PlatformAuditEvents");
}
