using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260925090000_AddPatternEngineV2")]
    public partial class AddPatternEngineV2 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some supported legacy databases contain migration-history rows for the
            // initial schema while the PatternDetections table itself is missing.
            // Recreate its v1 shape before applying the additive v2 migration.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "PatternDetections" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_PatternDetections" PRIMARY KEY AUTOINCREMENT,
                    "Symbol" TEXT NOT NULL,
                    "PatternType" INTEGER NOT NULL,
                    "DetectedAtUtc" TEXT NOT NULL,
                    "Details" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS "IX_PatternDetections_PatternType" ON "PatternDetections" ("PatternType");
                CREATE INDEX IF NOT EXISTS "IX_PatternDetections_Symbol" ON "PatternDetections" ("Symbol");
                """);

            migrationBuilder.AddColumn<int>(name: "Direction", table: "ReplaySignalRecords", type: "INTEGER", nullable: false, defaultValue: 1);

            migrationBuilder.AddColumn<int>(name: "Direction", table: "PatternDetections", type: "INTEGER", nullable: false, defaultValue: 1);
            migrationBuilder.AddColumn<string>(name: "InstrumentId", table: "PatternDetections", type: "TEXT", nullable: false, defaultValue: "", collation: "NOCASE");
            migrationBuilder.AddColumn<string>(name: "PatternKey", table: "PatternDetections", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(name: "PatternVersion", table: "PatternDetections", type: "TEXT", nullable: false, defaultValue: "patterns-v1");
            migrationBuilder.AddColumn<Guid>(name: "SignalId", table: "PatternDetections", type: "TEXT", nullable: false, defaultValue: Guid.Empty);
            migrationBuilder.AddColumn<string>(name: "StrategyId", table: "PatternDetections", type: "TEXT", nullable: false, defaultValue: "deterministic-patterns");
            migrationBuilder.AddColumn<int>(name: "Timeframe", table: "PatternDetections", type: "INTEGER", nullable: false, defaultValue: 0);

            migrationBuilder.Sql("UPDATE PatternDetections SET InstrumentId = Symbol, PatternKey = 'LEGACY|' || Id;");

            migrationBuilder.CreateIndex(name: "IX_PatternDetections_InstrumentId", table: "PatternDetections", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_PatternDetections_PatternKey", table: "PatternDetections", column: "PatternKey", unique: true, filter: "PatternKey <> ''");
            migrationBuilder.CreateIndex(name: "IX_PatternDetections_SignalId", table: "PatternDetections", column: "SignalId");
            migrationBuilder.CreateIndex(
                name: "IX_PatternDetections_InstrumentId_StrategyId_Timeframe_Direction_DetectedAtUtc",
                table: "PatternDetections",
                columns: new[] { "InstrumentId", "StrategyId", "Timeframe", "Direction", "DetectedAtUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_PatternDetections_InstrumentId", table: "PatternDetections");
            migrationBuilder.DropIndex(name: "IX_PatternDetections_PatternKey", table: "PatternDetections");
            migrationBuilder.DropIndex(name: "IX_PatternDetections_SignalId", table: "PatternDetections");
            migrationBuilder.DropIndex(name: "IX_PatternDetections_InstrumentId_StrategyId_Timeframe_Direction_DetectedAtUtc", table: "PatternDetections");

            migrationBuilder.DropColumn(name: "Direction", table: "ReplaySignalRecords");
            migrationBuilder.DropColumn(name: "Direction", table: "PatternDetections");
            migrationBuilder.DropColumn(name: "InstrumentId", table: "PatternDetections");
            migrationBuilder.DropColumn(name: "PatternKey", table: "PatternDetections");
            migrationBuilder.DropColumn(name: "PatternVersion", table: "PatternDetections");
            migrationBuilder.DropColumn(name: "SignalId", table: "PatternDetections");
            migrationBuilder.DropColumn(name: "StrategyId", table: "PatternDetections");
            migrationBuilder.DropColumn(name: "Timeframe", table: "PatternDetections");
        }
    }
}
