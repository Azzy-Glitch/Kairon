using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kairon.Backend.Migrations;

/// <summary>SQL Server counterpart of SqliteSchemaMigrator version 12.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261008110000_AddPairingSessionDefaults")]
public sealed class AddPairingSessionDefaults : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Environment", table: "SdkPairingSessions",
            type: "nvarchar(50)", maxLength: 50, nullable: true);
        migrationBuilder.AddColumn<string>(name: "Service", table: "SdkPairingSessions",
            type: "nvarchar(200)", maxLength: 200, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Environment", table: "SdkPairingSessions");
        migrationBuilder.DropColumn(name: "Service", table: "SdkPairingSessions");
    }
}
