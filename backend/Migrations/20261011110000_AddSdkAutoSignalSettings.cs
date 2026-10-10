using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kairon.Backend.Migrations;

/// <summary>SQL Server counterpart of SqliteSchemaMigrator version 15: automatic SDK signal settings
/// per paired app (credential), set from the desktop.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261011110000_AddSdkAutoSignalSettings")]
public sealed class AddSdkAutoSignalSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "AutoQueueDepth", table: "ProjectApiCredentials",
            type: "bit", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<bool>(name: "AutoRetries", table: "ProjectApiCredentials",
            type: "bit", nullable: false, defaultValue: true);
        migrationBuilder.AddColumn<int>(name: "RetryWindowSeconds", table: "ProjectApiCredentials",
            type: "int", nullable: false, defaultValue: 10);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RetryWindowSeconds", table: "ProjectApiCredentials");
        migrationBuilder.DropColumn(name: "AutoRetries", table: "ProjectApiCredentials");
        migrationBuilder.DropColumn(name: "AutoQueueDepth", table: "ProjectApiCredentials");
    }
}
