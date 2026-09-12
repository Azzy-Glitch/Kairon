using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairon.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddRemediationTargetEnvironmentNormalized : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RemediationTargets_ProjectId_Environment_Service",
                table: "RemediationTargets");

            migrationBuilder.AddColumn<string>(
                name: "EnvironmentNormalized",
                table: "RemediationTargets",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            // Backfill existing rows before the new unique index is created below - a fresh
            // install has no rows yet, and an upgrade must derive the normalized value from
            // whatever case-preserved Environment it already stored rather than leaving it blank.
            migrationBuilder.Sql(
                "UPDATE RemediationTargets SET EnvironmentNormalized = LOWER(Environment);");

            // If a legacy database already has two ENABLED targets for the same project/service
            // whose Environment differs only by case (e.g. "Production" and "production"), the
            // unique index below cannot be created without silently keeping one row and losing
            // the other - which this migration must never do. Fail loudly and require the
            // operator to manually disable or re-scope one of the conflicting rows first.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM RemediationTargets
                    WHERE Enabled = 1
                    GROUP BY ProjectId, EnvironmentNormalized, Service
                    HAVING COUNT(*) > 1
                )
                BEGIN
                    RAISERROR('Cannot enforce case-insensitive RemediationTarget uniqueness: conflicting enabled targets already exist whose Environment differs only by case for the same ProjectId/Service. Resolve manually (disable or rename one of each conflicting pair) before upgrading.', 16, 1);
                END
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RemediationTargets_ProjectId_EnvironmentNormalized_Service",
                table: "RemediationTargets",
                columns: new[] { "ProjectId", "EnvironmentNormalized", "Service" },
                unique: true,
                filter: "Enabled = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RemediationTargets_ProjectId_EnvironmentNormalized_Service",
                table: "RemediationTargets");

            migrationBuilder.DropColumn(
                name: "EnvironmentNormalized",
                table: "RemediationTargets");

            migrationBuilder.CreateIndex(
                name: "IX_RemediationTargets_ProjectId_Environment_Service",
                table: "RemediationTargets",
                columns: new[] { "ProjectId", "Environment", "Service" },
                unique: true,
                filter: "Enabled = 1");
        }
    }
}
