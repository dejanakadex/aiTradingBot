using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260925210000_AddResearchEvaluation")]
    public partial class AddResearchEvaluation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "MarketRegime", table: "ResearchCandidateRecords", type: "TEXT", nullable: false, defaultValue: "Unknown");
            migrationBuilder.AddColumn<decimal>(name: "NormalizedLiquidity", table: "ResearchCandidateRecords", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "NormalizedVolatility", table: "ResearchCandidateRecords", type: "TEXT", nullable: true);

            migrationBuilder.CreateTable(
                name: "ResearchEvaluationRunRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunKey = table.Column<string>(type: "TEXT", nullable: false),
                    ReplayRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    HorizonSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HoldoutStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceCandidateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EligibleCandidateCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WalkForwardFoldCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SelectedConfidenceThreshold = table.Column<decimal>(type: "TEXT", nullable: false),
                    SelectionReason = table.Column<string>(type: "TEXT", nullable: false),
                    EvaluationVersion = table.Column<string>(type: "TEXT", nullable: false),
                    FeatureVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PatternVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LabelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    InputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    OutputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    PreHoldoutMetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    WalkForwardMetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HoldoutMetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreHoldoutThresholdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FoldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CostSensitivityJson = table.Column<string>(type: "TEXT", nullable: false),
                    SegmentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ResearchEvaluationRunRecords", x => x.Id));

            migrationBuilder.CreateIndex("IX_ResearchEvaluationRunRecords_RunKey", "ResearchEvaluationRunRecords", "RunKey", unique: true);
            migrationBuilder.CreateIndex("IX_ResearchEvaluationRunRecords_ReplayRunId", "ResearchEvaluationRunRecords", "ReplayRunId");
            migrationBuilder.CreateIndex("IX_ResearchEvaluationRunRecords_EvaluationVersion", "ResearchEvaluationRunRecords", "EvaluationVersion");
            migrationBuilder.CreateIndex("IX_ResearchEvaluationRunRecords_InstrumentId_StrategyId_HorizonSeconds_CompletedAtUtc", "ResearchEvaluationRunRecords", new[] { "InstrumentId", "StrategyId", "HorizonSeconds", "CompletedAtUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ResearchEvaluationRunRecords");
            migrationBuilder.DropColumn(name: "MarketRegime", table: "ResearchCandidateRecords");
            migrationBuilder.DropColumn(name: "NormalizedLiquidity", table: "ResearchCandidateRecords");
            migrationBuilder.DropColumn(name: "NormalizedVolatility", table: "ResearchCandidateRecords");
        }
    }
}
