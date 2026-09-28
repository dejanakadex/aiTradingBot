using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260928210000_AddResearchCalibration")]
    public partial class AddResearchCalibration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchCalibrationProfileRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileKey = table.Column<string>(type: "TEXT", nullable: false),
                    ModelVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReplayRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    HorizonSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CalibrationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    EvaluationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    FeatureVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PatternVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LabelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    StableConfidenceThreshold = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinimumCalibratedProbability = table.Column<decimal>(type: "TEXT", nullable: false),
                    AverageWinBps = table.Column<decimal>(type: "TEXT", nullable: false),
                    AverageLossBps = table.Column<decimal>(type: "TEXT", nullable: false),
                    AverageEstimatedCostBps = table.Column<decimal>(type: "TEXT", nullable: false),
                    ThresholdStabilityJson = table.Column<string>(type: "TEXT", nullable: false),
                    CalibrationPointsJson = table.Column<string>(type: "TEXT", nullable: false),
                    WalkForwardRawMetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    WalkForwardCalibratedMetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HoldoutRawMetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HoldoutCalibratedMetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    BaselineComparisonJson = table.Column<string>(type: "TEXT", nullable: false),
                    InputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    OutputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_ResearchCalibrationProfileRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "ResearchCalibrationApprovalRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    CalibrationProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Reviewer = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedOutputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    RelatedProfileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchCalibrationApprovalRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResearchCalibrationApprovalRecords_ResearchCalibrationProfileRecords_CalibrationProfileId",
                        column: x => x.CalibrationProfileId,
                        principalTable: "ResearchCalibrationProfileRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_ResearchCalibrationProfileRecords_ProfileKey", "ResearchCalibrationProfileRecords", "ProfileKey", unique: true);
            migrationBuilder.CreateIndex("IX_ResearchCalibrationProfileRecords_EvaluationRunId", "ResearchCalibrationProfileRecords", "EvaluationRunId");
            migrationBuilder.CreateIndex("IX_ResearchCalibrationProfileRecords_Status", "ResearchCalibrationProfileRecords", "Status");
            migrationBuilder.CreateIndex("IX_ResearchCalibrationProfileRecords_CalibrationVersion", "ResearchCalibrationProfileRecords", "CalibrationVersion");
            migrationBuilder.CreateIndex("IX_ResearchCalibrationProfileRecords_InstrumentId_StrategyId_HorizonSeconds_ModelVersion", "ResearchCalibrationProfileRecords", new[] { "InstrumentId", "StrategyId", "HorizonSeconds", "ModelVersion" });
            migrationBuilder.CreateIndex("IX_ResearchCalibrationApprovalRecords_CalibrationProfileId_Revision", "ResearchCalibrationApprovalRecords", new[] { "CalibrationProfileId", "Revision" }, unique: true);
            migrationBuilder.CreateIndex("IX_ResearchCalibrationApprovalRecords_CreatedAtUtc", "ResearchCalibrationApprovalRecords", "CreatedAtUtc");
            migrationBuilder.CreateIndex("IX_ResearchCalibrationApprovalRecords_Action", "ResearchCalibrationApprovalRecords", "Action");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ResearchCalibrationApprovalRecords");
            migrationBuilder.DropTable(name: "ResearchCalibrationProfileRecords");
        }
    }
}
