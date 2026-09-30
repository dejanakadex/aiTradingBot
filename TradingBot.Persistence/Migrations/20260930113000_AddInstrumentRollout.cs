using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    public partial class AddInstrumentRollout : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstrumentRolloutEvaluationRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    StatusBefore = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusAfter = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ShadowDecisionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ApprovedShadowDecisionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PaperOrderCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FilledPaperOrderCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PaperUnfilledRatio = table.Column<decimal>(type: "TEXT", nullable: false),
                    AverageEntrySlippageBps = table.Column<decimal>(type: "TEXT", nullable: true),
                    AverageFillLatencyMilliseconds = table.Column<double>(type: "REAL", nullable: true),
                    QualityIncidentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnhealthyStreamCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UnknownOrderCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ShadowCriteriaPassed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PaperCriteriaPassed = table.Column<bool>(type: "INTEGER", nullable: false),
                    EligibleForManualLiveApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    Suspended = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReasonsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_InstrumentRolloutEvaluationRecords", x => x.Id));

            migrationBuilder.CreateIndex("IX_InstrumentRolloutEvaluationRecords_EvaluatedAtUtc", "InstrumentRolloutEvaluationRecords", "EvaluatedAtUtc");
            migrationBuilder.CreateIndex("IX_InstrumentRolloutEvaluationRecords_InstrumentId", "InstrumentRolloutEvaluationRecords", "InstrumentId");
            migrationBuilder.CreateIndex("IX_InstrumentRolloutEvaluationRecords_StatusAfter", "InstrumentRolloutEvaluationRecords", "StatusAfter");
            migrationBuilder.CreateIndex("IX_InstrumentRolloutEvaluationRecords_Suspended", "InstrumentRolloutEvaluationRecords", "Suspended");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "InstrumentRolloutEvaluationRecords");
        }
    }
}
