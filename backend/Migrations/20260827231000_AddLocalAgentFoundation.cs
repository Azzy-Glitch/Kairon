using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using AIDIP.Backend.Infrastructure;

#nullable disable

namespace AIDIP.Backend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260827231000_AddLocalAgentFoundation")]
public partial class AddLocalAgentFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var sqlite = ActiveProvider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        var guid = sqlite ? "TEXT" : "uniqueidentifier";
        var text50 = sqlite ? "TEXT" : "nvarchar(50)";
        var text128 = sqlite ? "TEXT" : "nvarchar(128)";
        var text255 = sqlite ? "TEXT" : "nvarchar(255)";
        var text500 = sqlite ? "TEXT" : "nvarchar(500)";
        var text2000 = sqlite ? "TEXT" : "nvarchar(2000)";
        var timestamp = sqlite ? "TEXT" : "datetime2";
        var integer = sqlite ? "INTEGER" : "int";
        var bigInteger = sqlite ? "INTEGER" : "bigint";
        var real = sqlite ? "REAL" : "float";
        var boolean = sqlite ? "INTEGER" : "bit";

        migrationBuilder.CreateTable(
            name: "Machines",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guid, nullable: false),
                HostName = table.Column<string>(type: text255, maxLength: 255, nullable: false),
                OperatingSystem = table.Column<string>(type: text500, maxLength: 500, nullable: false),
                Architecture = table.Column<string>(type: text50, maxLength: 50, nullable: false),
                AgentVersion = table.Column<string>(type: text50, maxLength: 50, nullable: false),
                AgentCredentialHash = table.Column<string>(type: text128, maxLength: 128, nullable: false),
                RegisteredAt = table.Column<DateTime>(type: timestamp, nullable: false),
                LastSeenAt = table.Column<DateTime>(type: timestamp, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Machines", x => x.Id));

        migrationBuilder.CreateTable(
            name: "DiscoveredApplications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guid, nullable: false),
                MachineId = table.Column<Guid>(type: guid, nullable: false),
                ProcessId = table.Column<int>(type: integer, nullable: false),
                ProcessStartedAt = table.Column<DateTime>(type: timestamp, nullable: false),
                Name = table.Column<string>(type: text255, maxLength: 255, nullable: false),
                Executable = table.Column<string>(type: text2000, maxLength: 2000, nullable: false),
                Runtime = table.Column<string>(type: text50, maxLength: 50, nullable: false),
                CpuPercent = table.Column<double>(type: real, nullable: false),
                MemoryBytes = table.Column<long>(type: bigInteger, nullable: false),
                IsRunning = table.Column<bool>(type: boolean, nullable: false),
                FirstSeenAt = table.Column<DateTime>(type: timestamp, nullable: false),
                LastSeenAt = table.Column<DateTime>(type: timestamp, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscoveredApplications", x => x.Id);
                table.ForeignKey("FK_DiscoveredApplications_Machines_MachineId", x => x.MachineId,
                    "Machines", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Machines_LastSeenAt", "Machines", "LastSeenAt");
        migrationBuilder.CreateIndex("IX_DiscoveredApplications_MachineId_IsRunning", "DiscoveredApplications",
            new[] { "MachineId", "IsRunning" });
        migrationBuilder.CreateIndex("IX_DiscoveredApplications_MachineId_ProcessId_ProcessStartedAt",
            "DiscoveredApplications", new[] { "MachineId", "ProcessId", "ProcessStartedAt" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("DiscoveredApplications");
        migrationBuilder.DropTable("Machines");
    }
}
