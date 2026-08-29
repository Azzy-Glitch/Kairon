using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairon.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMachineDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscoveredApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<int>(type: "int", nullable: false),
                    ProcessStartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Executable = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Runtime = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CpuPercent = table.Column<double>(type: "float", nullable: false),
                    MemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsRunning = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Machines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HostName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    OperatingSystem = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Architecture = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AgentVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    AgentCredentialHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Machines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredApplications_MachineId",
                table: "DiscoveredApplications",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredApplications_MachineId_ProcessId_ProcessStartedAt",
                table: "DiscoveredApplications",
                columns: new[] { "MachineId", "ProcessId", "ProcessStartedAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveredApplications");

            migrationBuilder.DropTable(
                name: "Machines");
        }
    }
}
