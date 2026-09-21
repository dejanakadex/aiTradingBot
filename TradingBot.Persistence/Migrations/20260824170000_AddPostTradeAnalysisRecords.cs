using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    public partial class AddPostTradeAnalysisRecords : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostTradeAnalysisRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceTradeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    OpenedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    GrossProfitLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    CommissionsFees = table.Column<decimal>(type: "TEXT", nullable: false),
                    NetProfitLoss = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaximumFavorableExcursion = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaximumAdverseExcursion = table.Column<decimal>(type: "TEXT", nullable: true),
                    HoldingDurationMinutes = table.Column<decimal>(type: "TEXT", nullable: false),
                    MarketRegime = table.Column<string>(type: "TEXT", nullable: false),
                    AboveVwap = table.Column<bool>(type: "INTEGER", nullable: true),
                    FifteenMinuteTrend = table.Column<string>(type: "TEXT", nullable: false),
                    AiDecision = table.Column<string>(type: "TEXT", nullable: false),
                    AiConfidence = table.Column<decimal>(type: "TEXT", nullable: true),
                    CriticDecision = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyDecision = table.Column<string>(type: "TEXT", nullable: false),
                    RiskDecision = table.Column<string>(type: "TEXT", nullable: false),
                    TradeResult = table.Column<string>(type: "TEXT", nullable: false),
                    MarketSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    PatternCandidateJson = table.Column<string>(type: "TEXT", nullable: false),
                    AiMarketAnalysisJson = table.Column<string>(type: "TEXT", nullable: false),
                    AiCriticAnalysisJson = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyDecisionJson = table.Column<string>(type: "TEXT", nullable: false),
                    RiskDecisionJson = table.Column<string>(type: "TEXT", nullable: false),
                    OrderDataJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutionDataJson = table.Column<string>(type: "TEXT", nullable: false),
                    TradeResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostTradeAnalysisRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_AboveVwap", table: "PostTradeAnalysisRecords", column: "AboveVwap");
            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_AiDecision", table: "PostTradeAnalysisRecords", column: "AiDecision");
            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_ClosedUtc", table: "PostTradeAnalysisRecords", column: "ClosedUtc");
            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_FifteenMinuteTrend", table: "PostTradeAnalysisRecords", column: "FifteenMinuteTrend");
            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_MarketRegime", table: "PostTradeAnalysisRecords", column: "MarketRegime");
            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_Pattern", table: "PostTradeAnalysisRecords", column: "Pattern");
            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_SourceTradeId", table: "PostTradeAnalysisRecords", column: "SourceTradeId");
            migrationBuilder.CreateIndex(name: "IX_PostTradeAnalysisRecords_Symbol", table: "PostTradeAnalysisRecords", column: "Symbol");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PostTradeAnalysisRecords");
        }
    }
}
