using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261123150000_AddAutomaticPostTradeAnalysis")]
public sealed class AddAutomaticPostTradeAnalysis : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.AddColumn<int>("ExitManagementId", "PostTradeAnalysisRecords", type: "INTEGER", nullable: true);
        b.AddColumn<Guid>("SignalId", "PostTradeAnalysisRecords", type: "TEXT", nullable: true);
        b.AddColumn<bool>("FeesComplete", "PostTradeAnalysisRecords", type: "INTEGER", nullable: true);
        b.AddColumn<DateTime>("UpdatedUtc", "PostTradeAnalysisRecords", type: "TEXT", nullable: true);
        foreach (var name in new[] { "InstrumentId", "StrategyId", "InputHash", "EntryContextJson" })
            b.AddColumn<string>(name, "PostTradeAnalysisRecords", type: "TEXT", nullable: false, defaultValue: "");
        // Nullable key preserves all existing manual records, including historic SourceTradeId duplicates.
        b.CreateIndex("IX_PostTradeAnalysisRecords_ExitManagementId", "PostTradeAnalysisRecords", "ExitManagementId", unique: true);
        b.CreateIndex("IX_PostTradeAnalysisRecords_AutomaticSourceTrade", "PostTradeAnalysisRecords", "SourceTradeId", unique: true, filter: "\"ExitManagementId\" IS NOT NULL");
    }
    protected override void Down(MigrationBuilder b)
    {
        b.DropIndex("IX_PostTradeAnalysisRecords_ExitManagementId", "PostTradeAnalysisRecords");
        b.DropIndex("IX_PostTradeAnalysisRecords_AutomaticSourceTrade", "PostTradeAnalysisRecords");
        foreach (var name in new[] { "ExitManagementId", "SignalId", "FeesComplete", "UpdatedUtc", "InstrumentId", "StrategyId", "InputHash", "EntryContextJson" })
            b.DropColumn(name, "PostTradeAnalysisRecords");
    }
}
