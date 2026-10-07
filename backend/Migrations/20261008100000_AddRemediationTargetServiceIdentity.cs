using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kairon.Backend.Migrations;

/// <summary>SQL Server counterpart of SqliteSchemaMigrator version 11. Existing targets are not
/// backfilled: an operator must re-confirm each one against the live service before it executes.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261008100000_AddRemediationTargetServiceIdentity")]
public sealed class AddRemediationTargetServiceIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "ServiceIdentityHash", table: "RemediationTargets",
            type: "nvarchar(64)", maxLength: 64, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ServiceIdentityHash", table: "RemediationTargets");
    }
}
