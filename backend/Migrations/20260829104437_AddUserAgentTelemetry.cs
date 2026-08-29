using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairon.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAgentTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscoveredApplications_MachineId_ProcessId_ProcessStartedAt",
                table: "DiscoveredApplications");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUserAgentSeenAt",
                table: "Machines",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentProcessId",
                table: "DiscoveredApplications",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SessionId",
                table: "DiscoveredApplications",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "DiscoveredApplications",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "MachineAgent");

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                table: "DiscoveredApplications",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredApplications_MachineId_ProcessId_ProcessStartedAt_Source",
                table: "DiscoveredApplications",
                columns: new[] { "MachineId", "ProcessId", "ProcessStartedAt", "Source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DiscoveredApplications_MachineId_ProcessId_ProcessStartedAt_Source",
                table: "DiscoveredApplications");

            migrationBuilder.DropColumn(
                name: "LastUserAgentSeenAt",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "ParentProcessId",
                table: "DiscoveredApplications");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "DiscoveredApplications");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "DiscoveredApplications");

            migrationBuilder.DropColumn(
                name: "UserName",
                table: "DiscoveredApplications");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredApplications_MachineId_ProcessId_ProcessStartedAt",
                table: "DiscoveredApplications",
                columns: new[] { "MachineId", "ProcessId", "ProcessStartedAt" },
                unique: true);
        }
    }
}
