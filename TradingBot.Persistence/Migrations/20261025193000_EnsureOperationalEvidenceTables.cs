using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261025193000_EnsureOperationalEvidenceTables")]
public sealed class EnsureOperationalEvidenceTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Historical rollout/post-trade migrations lack EF discovery metadata. Use a forward
        // repair so existing migrated databases and databases with rollout evidence both survive.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "PostTradeAnalysisRecords" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PostTradeAnalysisRecords" PRIMARY KEY AUTOINCREMENT,
                "SourceTradeId" INTEGER NULL,
                "Symbol" TEXT NOT NULL,
                "Pattern" TEXT NOT NULL,
                "OpenedUtc" TEXT NOT NULL,
                "ClosedUtc" TEXT NOT NULL,
                "Quantity" TEXT NOT NULL,
                "EntryPrice" TEXT NOT NULL,
                "ExitPrice" TEXT NOT NULL,
                "GrossProfitLoss" TEXT NOT NULL,
                "CommissionsFees" TEXT NOT NULL,
                "NetProfitLoss" TEXT NOT NULL,
                "MaximumFavorableExcursion" TEXT NULL,
                "MaximumAdverseExcursion" TEXT NULL,
                "HoldingDurationMinutes" TEXT NOT NULL,
                "MarketRegime" TEXT NOT NULL,
                "AboveVwap" INTEGER NULL,
                "FifteenMinuteTrend" TEXT NOT NULL,
                "AiDecision" TEXT NOT NULL,
                "AiConfidence" TEXT NULL,
                "CriticDecision" TEXT NOT NULL,
                "StrategyDecision" TEXT NOT NULL,
                "RiskDecision" TEXT NOT NULL,
                "TradeResult" TEXT NOT NULL,
                "MarketSnapshotJson" TEXT NOT NULL,
                "PatternCandidateJson" TEXT NOT NULL,
                "AiMarketAnalysisJson" TEXT NOT NULL,
                "AiCriticAnalysisJson" TEXT NOT NULL,
                "StrategyDecisionJson" TEXT NOT NULL,
                "RiskDecisionJson" TEXT NOT NULL,
                "OrderDataJson" TEXT NOT NULL,
                "ExecutionDataJson" TEXT NOT NULL,
                "TradeResultJson" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_AboveVwap" ON "PostTradeAnalysisRecords" ("AboveVwap");
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_AiDecision" ON "PostTradeAnalysisRecords" ("AiDecision");
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_ClosedUtc" ON "PostTradeAnalysisRecords" ("ClosedUtc");
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_FifteenMinuteTrend" ON "PostTradeAnalysisRecords" ("FifteenMinuteTrend");
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_MarketRegime" ON "PostTradeAnalysisRecords" ("MarketRegime");
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_Pattern" ON "PostTradeAnalysisRecords" ("Pattern");
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_SourceTradeId" ON "PostTradeAnalysisRecords" ("SourceTradeId");
            CREATE INDEX IF NOT EXISTS "IX_PostTradeAnalysisRecords_Symbol" ON "PostTradeAnalysisRecords" ("Symbol");
            CREATE TABLE IF NOT EXISTS "InstrumentRolloutEvaluationRecords" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_InstrumentRolloutEvaluationRecords" PRIMARY KEY AUTOINCREMENT,
                "InstrumentId" TEXT NOT NULL,
                "Symbol" TEXT NOT NULL,
                "StatusBefore" INTEGER NOT NULL,
                "StatusAfter" INTEGER NOT NULL,
                "EvaluatedAtUtc" TEXT NOT NULL,
                "WindowStartUtc" TEXT NOT NULL,
                "ShadowDecisionCount" INTEGER NOT NULL,
                "ApprovedShadowDecisionCount" INTEGER NOT NULL,
                "PaperOrderCount" INTEGER NOT NULL,
                "FilledPaperOrderCount" INTEGER NOT NULL,
                "PaperUnfilledRatio" TEXT NOT NULL,
                "AverageEntrySlippageBps" TEXT NULL,
                "AverageFillLatencyMilliseconds" REAL NULL,
                "QualityIncidentCount" INTEGER NOT NULL,
                "UnhealthyStreamCount" INTEGER NOT NULL,
                "UnknownOrderCount" INTEGER NOT NULL,
                "ShadowCriteriaPassed" INTEGER NOT NULL,
                "PaperCriteriaPassed" INTEGER NOT NULL,
                "EligibleForManualLiveApproval" INTEGER NOT NULL,
                "Suspended" INTEGER NOT NULL,
                "ReasonsJson" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_InstrumentRolloutEvaluationRecords_EvaluatedAtUtc" ON "InstrumentRolloutEvaluationRecords" ("EvaluatedAtUtc");
            CREATE INDEX IF NOT EXISTS "IX_InstrumentRolloutEvaluationRecords_InstrumentId" ON "InstrumentRolloutEvaluationRecords" ("InstrumentId");
            CREATE INDEX IF NOT EXISTS "IX_InstrumentRolloutEvaluationRecords_StatusAfter" ON "InstrumentRolloutEvaluationRecords" ("StatusAfter");
            CREATE INDEX IF NOT EXISTS "IX_InstrumentRolloutEvaluationRecords_Suspended" ON "InstrumentRolloutEvaluationRecords" ("Suspended");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not erase pre-existing evidence when reverting this compatibility repair.
    }
}
