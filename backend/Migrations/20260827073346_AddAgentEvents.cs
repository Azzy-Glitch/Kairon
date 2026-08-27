using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairon.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Application = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Component = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEvents_Environment",
                table: "AgentEvents",
                column: "Environment");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEvents_EventType",
                table: "AgentEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEvents_ProjectId_Timestamp",
                table: "AgentEvents",
                columns: new[] { "ProjectId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEvents");
        }
    }
}
