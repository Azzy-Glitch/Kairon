using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kairon.Backend.Migrations;

/// <summary>SQL Server counterpart of SqliteSchemaMigrator version 13: application-process
/// remediation (target kind, Agent-observed process ids, and the UserAgent restart queue).</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261010100000_AddApplicationProcessRemediation")]
public sealed class AddApplicationProcessRemediation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Kind", table: "RemediationTargets",
            type: "nvarchar(30)", maxLength: 30, nullable: false, defaultValue: "WindowsService");
        migrationBuilder.AddColumn<int>(name: "ProcessId", table: "SdkMachineProofChallenges",
            type: "int", nullable: true);
        migrationBuilder.AddColumn<int>(name: "ProcessId", table: "SdkMachineBindings",
            type: "int", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProcessWorkingDirectory", table: "SdkMachineBindings",
            type: "nvarchar(1024)", maxLength: 1024, nullable: true);
        migrationBuilder.AddColumn<string>(name: "ProcessExecutable", table: "SdkMachineBindings",
            type: "nvarchar(1024)", maxLength: 1024, nullable: true);

        migrationBuilder.CreateTable(
            name: "ProcessRestartCommands",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActionKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SessionId = table.Column<int>(type: "int", nullable: false),
                ProcessId = table.Column<int>(type: "int", nullable: false),
                ProcessStartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                Executable = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                WorkingDirectory = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                NewProcessId = table.Column<int>(type: "int", nullable: true),
                Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                RowVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ProcessRestartCommands", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_ProcessRestartCommands_MachineId_SessionId_Status",
            table: "ProcessRestartCommands", columns: new[] { "MachineId", "SessionId", "Status" });
        migrationBuilder.CreateIndex(name: "IX_ProcessRestartCommands_IncidentId_ActionKey",
            table: "ProcessRestartCommands", columns: new[] { "IncidentId", "ActionKey" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProcessRestartCommands");
        migrationBuilder.DropColumn(name: "ProcessExecutable", table: "SdkMachineBindings");
        migrationBuilder.DropColumn(name: "ProcessWorkingDirectory", table: "SdkMachineBindings");
        migrationBuilder.DropColumn(name: "ProcessId", table: "SdkMachineBindings");
        migrationBuilder.DropColumn(name: "ProcessId", table: "SdkMachineProofChallenges");
        migrationBuilder.DropColumn(name: "Kind", table: "RemediationTargets");
    }
}
