using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kairon.Backend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004100000_AddAgentConfirmedSdkMachineBindings")]
public sealed class AddAgentConfirmedSdkMachineBindings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "MachineId", table: "TelemetryReceipts",
            type: "uniqueidentifier", nullable: true);
        migrationBuilder.CreateTable(
            name: "SdkMachineBindings",
            columns: table => new
            {
                CredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AgentCredentialHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                LastConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SdkMachineBindings", x => x.CredentialId));
        migrationBuilder.CreateIndex(
            name: "IX_SdkMachineBindings_ProjectId_MachineId",
            table: "SdkMachineBindings", columns: new[] { "ProjectId", "MachineId" });

        migrationBuilder.CreateTable(
            name: "SdkMachineProofChallenges",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EnvironmentNormalized = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                BodySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                AgentCredentialHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                RowVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SdkMachineProofChallenges", x => x.Id));
        migrationBuilder.CreateIndex(
            name: "IX_SdkMachineProofChallenges_ExpiresAt",
            table: "SdkMachineProofChallenges", column: "ExpiresAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "MachineId", table: "TelemetryReceipts");
        migrationBuilder.DropTable("SdkMachineProofChallenges");
        migrationBuilder.DropTable("SdkMachineBindings");
    }
}
