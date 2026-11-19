using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261119220000_AddForexCooldownIndex")]
public sealed class AddForexCooldownIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex(
        "IX_ResearchCandidate_FxCooldown", "ResearchCandidateRecords",
        ["ReplayRunId", "InstrumentId", "StrategyId", "Timeframe", "DecisionStage", "Outcome", "EvaluatedAtUtc"]);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropIndex("IX_ResearchCandidate_FxCooldown", "ResearchCandidateRecords");
}
