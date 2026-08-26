using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairon.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentDiagnosisStale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DiagnosisStale",
                table: "SreIncidents",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiagnosisStale",
                table: "SreIncidents");
        }
    }
}
