using Kairon.Backend.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kairon.Backend.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260906120000_AddTelemetryMachineScope")]
public sealed class AddTelemetryMachineScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.AddColumn<Guid>("MachineId", "Incidents", type: "uniqueidentifier", nullable: true);
        migrationBuilder.AddColumn<Guid>("MachineId", "Metrics", type: "uniqueidentifier", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropColumn("MachineId", "Incidents");
        migrationBuilder.DropColumn("MachineId", "Metrics");
    }
}
