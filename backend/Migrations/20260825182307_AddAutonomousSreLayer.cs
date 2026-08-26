using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kairon.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAutonomousSreLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Application",
                table: "Metrics",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Component",
                table: "Metrics",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueDepth",
                table: "Metrics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RetryCount",
                table: "Metrics",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "Metrics",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Application",
                table: "Incidents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "Incidents",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SreIncidentId",
                table: "Incidents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SreIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Application = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Service = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    AffectedComponent = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AffectedEndpoint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SymptomsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    TelemetryReferencesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CorrelatedMetricsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16000, nullable: false),
                    RootCause = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ContributingFactorsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: true),
                    PredictedImpact = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PredictedRisk = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    RecommendationsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16000, nullable: true),
                    RemediationState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    VerificationState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CorrelationKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SignalCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SreIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IncidentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreviousState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    NewState = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ActionId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DataJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentEvents_SreIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "SreIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CollectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16000, nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentEvidence_SreIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "SreIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RemediationActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionKey = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActionType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ExpectedOutcome = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RiskLevel = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PolicyDecision = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RejectionReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExecutionResult = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExecutionError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    VerificationResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemediationActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemediationActions_SreIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "SreIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VerificationResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ComparisonsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    RecoveryScore = table.Column<double>(type: "float", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificationResults_SreIncidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "SreIncidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_SreIncidentId",
                table: "Incidents",
                column: "SreIncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvents_IncidentId_Timestamp",
                table: "IncidentEvents",
                columns: new[] { "IncidentId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentEvidence_IncidentId_Kind",
                table: "IncidentEvidence",
                columns: new[] { "IncidentId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_RemediationActions_ActionKey",
                table: "RemediationActions",
                column: "ActionKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemediationActions_IncidentId_Status",
                table: "RemediationActions",
                columns: new[] { "IncidentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SreIncidents_CorrelationKey",
                table: "SreIncidents",
                column: "CorrelationKey");

            migrationBuilder.CreateIndex(
                name: "IX_SreIncidents_Environment_Service",
                table: "SreIncidents",
                columns: new[] { "Environment", "Service" });

            migrationBuilder.CreateIndex(
                name: "IX_SreIncidents_IncidentKey",
                table: "SreIncidents",
                column: "IncidentKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SreIncidents_ProjectId_Timestamp",
                table: "SreIncidents",
                columns: new[] { "ProjectId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SreIncidents_Status",
                table: "SreIncidents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_IncidentId_StartedAt",
                table: "VerificationResults",
                columns: new[] { "IncidentId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentEvents");

            migrationBuilder.DropTable(
                name: "IncidentEvidence");

            migrationBuilder.DropTable(
                name: "RemediationActions");

            migrationBuilder.DropTable(
                name: "VerificationResults");

            migrationBuilder.DropTable(
                name: "SreIncidents");

            migrationBuilder.DropIndex(
                name: "IX_Incidents_SreIncidentId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "Application",
                table: "Metrics");

            migrationBuilder.DropColumn(
                name: "Component",
                table: "Metrics");

            migrationBuilder.DropColumn(
                name: "QueueDepth",
                table: "Metrics");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "Metrics");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "Metrics");

            migrationBuilder.DropColumn(
                name: "Application",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "SreIncidentId",
                table: "Incidents");
        }
    }
}
