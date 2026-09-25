using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260925100000_AddCandidateResearchLabels")]
    public partial class AddCandidateResearchLabels : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchCandidateRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    RecordKey = table.Column<string>(type: "TEXT", nullable: false),
                    CandidateKey = table.Column<string>(type: "TEXT", nullable: false),
                    ReplayRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SignalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceEventId = table.Column<string>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    PatternType = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Timeframe = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Confidence = table.Column<decimal>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    DecisionStage = table.Column<string>(type: "TEXT", nullable: false),
                    FeatureVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PatternVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LabelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    HardConditionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ScoreComponentsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ResearchCandidateRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "CandidateLabelRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    ResearchCandidateId = table.Column<long>(type: "INTEGER", nullable: false),
                    HorizonSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetStopOutcome = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaximumFavorableExcursionBps = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaximumAdverseExcursionBps = table.Column<decimal>(type: "TEXT", nullable: true),
                    GrossReturnBps = table.Column<decimal>(type: "TEXT", nullable: true),
                    EstimatedCostBps = table.Column<decimal>(type: "TEXT", nullable: true),
                    NetReturnBps = table.Column<decimal>(type: "TEXT", nullable: true),
                    ObservedSpreadBps = table.Column<decimal>(type: "TEXT", nullable: true),
                    FirstTargetEventId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstStopEventId = table.Column<string>(type: "TEXT", nullable: false),
                    MaximumEventTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LabelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    CalculatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateLabelRecords", x => x.Id);
                    table.ForeignKey("FK_CandidateLabelRecords_ResearchCandidateRecords_ResearchCandidateId", x => x.ResearchCandidateId, "ResearchCandidateRecords", "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_ResearchCandidateRecords_RecordKey", "ResearchCandidateRecords", "RecordKey", unique: true);
            migrationBuilder.CreateIndex("IX_ResearchCandidateRecords_CandidateKey", "ResearchCandidateRecords", "CandidateKey");
            migrationBuilder.CreateIndex("IX_ResearchCandidateRecords_ReplayRunId", "ResearchCandidateRecords", "ReplayRunId");
            migrationBuilder.CreateIndex("IX_ResearchCandidateRecords_InstrumentId", "ResearchCandidateRecords", "InstrumentId");
            migrationBuilder.CreateIndex("IX_ResearchCandidateRecords_Outcome", "ResearchCandidateRecords", "Outcome");
            migrationBuilder.CreateIndex("IX_ResearchCandidateRecords_InstrumentId_StrategyId_Timeframe_Direction_EvaluatedAtUtc", "ResearchCandidateRecords", new[] { "InstrumentId", "StrategyId", "Timeframe", "Direction", "EvaluatedAtUtc" });
            migrationBuilder.CreateIndex("IX_CandidateLabelRecords_ResearchCandidateId_HorizonSeconds", "CandidateLabelRecords", new[] { "ResearchCandidateId", "HorizonSeconds" }, unique: true);
            migrationBuilder.CreateIndex("IX_CandidateLabelRecords_Status_WindowEndUtc", "CandidateLabelRecords", new[] { "Status", "WindowEndUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CandidateLabelRecords");
            migrationBuilder.DropTable(name: "ResearchCandidateRecords");
        }
    }
}
