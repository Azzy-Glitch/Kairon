using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIDIP.Backend.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Analyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Input = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OutputJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16000, nullable: false),
                    Score = table.Column<double>(type: "float", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Analyses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ErrorType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    StackTrace = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    RequestId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Environment = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Resolved = table.Column<bool>(type: "bit", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Metrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CpuPercent = table.Column<double>(type: "float", nullable: true),
                    MemoryPercent = table.Column<double>(type: "float", nullable: true),
                    ResponseTimeMs = table.Column<double>(type: "float", nullable: true),
                    RequestCount = table.Column<long>(type: "bigint", nullable: false),
                    ErrorCount = table.Column<long>(type: "bigint", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Metrics", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_CreatedAt",
                table: "Analyses",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Analyses_ProjectId_Type",
                table: "Analyses",
                columns: new[] { "ProjectId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Endpoint",
                table: "Incidents",
                column: "Endpoint");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Environment",
                table: "Incidents",
                column: "Environment");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ProjectId_Timestamp",
                table: "Incidents",
                columns: new[] { "ProjectId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_StatusCode",
                table: "Incidents",
                column: "StatusCode");

            migrationBuilder.CreateIndex(
                name: "IX_Metrics_Environment",
                table: "Metrics",
                column: "Environment");

            migrationBuilder.CreateIndex(
                name: "IX_Metrics_ProjectId_Timestamp",
                table: "Metrics",
                columns: new[] { "ProjectId", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Analyses");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "Metrics");
        }
    }
}
