using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kairon.Backend.Migrations;

/// <summary>SQL Server counterpart of SqliteSchemaMigrator version 14: incidents keep the AI's
/// structured assessment (root-cause certainty, next steps, actions it weighed).</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261011100000_AddIncidentAssessment")]
public sealed class AddIncidentAssessment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "AssessmentJson", table: "SreIncidents",
            type: "nvarchar(max)", maxLength: 16000, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AssessmentJson", table: "SreIncidents");
    }
}
