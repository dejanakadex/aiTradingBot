using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260924160000_AddDeterministicReplay")]
    public partial class AddDeterministicReplay : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReplayRunRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    FromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SpeedMultiplier = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    InputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    InputEventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedEventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SignalCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckpointEventId = table.Column<string>(type: "TEXT", nullable: false),
                    CheckpointEventTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CheckpointReceivedTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OutputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    MarketDataVersion = table.Column<string>(type: "TEXT", nullable: false),
                    FeatureVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PatternVersion = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ReplayRunRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "ReplaySignalRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    ReplayRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", nullable: false),
                    PatternType = table.Column<int>(type: "INTEGER", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframe = table.Column<int>(type: "INTEGER", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Confidence = table.Column<decimal>(type: "TEXT", nullable: false),
                    RelevantPriceLevelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    FeaturesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ReplaySignalRecords", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_ReplayRunRecords_CreatedAtUtc", table: "ReplayRunRecords", column: "CreatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_ReplayRunRecords_InstrumentId", table: "ReplayRunRecords", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_ReplayRunRecords_Status", table: "ReplayRunRecords", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_ReplayRunRecords_UpdatedAtUtc", table: "ReplayRunRecords", column: "UpdatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_ReplaySignalRecords_ReplayRunId_DetectedAtUtc", table: "ReplaySignalRecords", columns: new[] { "ReplayRunId", "DetectedAtUtc" });
            migrationBuilder.CreateIndex(name: "IX_ReplaySignalRecords_ReplayRunId_SignalId", table: "ReplaySignalRecords", columns: new[] { "ReplayRunId", "SignalId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_ReplaySignalRecords_SourceEventId", table: "ReplaySignalRecords", column: "SourceEventId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ReplaySignalRecords");
            migrationBuilder.DropTable(name: "ReplayRunRecords");
        }
    }
}
