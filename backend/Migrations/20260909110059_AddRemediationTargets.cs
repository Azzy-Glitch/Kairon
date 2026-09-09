using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairon.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddRemediationTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemediationTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MachineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TelemetryCredentialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedHostName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    WindowsServiceName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AllowedOperationsJson = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemediationTargets_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemediationTargets_MachineId",
                table: "RemediationTargets",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_RemediationTargets_ProjectId_Environment_Service",
                table: "RemediationTargets",
                columns: new[] { "ProjectId", "Environment", "Service" },
                unique: true,
                filter: "Enabled = 1");

            migrationBuilder.CreateIndex(
                name: "IX_RemediationTargets_TelemetryCredentialId",
                table: "RemediationTargets",
                column: "TelemetryCredentialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemediationTargets");
        }
    }
}
