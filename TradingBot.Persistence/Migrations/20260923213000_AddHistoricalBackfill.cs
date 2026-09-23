using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260923213000_AddHistoricalBackfill")]
    public partial class AddHistoricalBackfill : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricalBackfillJobRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DesiredStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DesiredEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextSegmentEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedSegments = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    BarsReceived = table.Column<long>(type: "INTEGER", nullable: false),
                    BarsInserted = table.Column<long>(type: "INTEGER", nullable: false),
                    DuplicateBars = table.Column<long>(type: "INTEGER", nullable: false),
                    GapCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_HistoricalBackfillJobRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "HistoricalBackfillSegmentRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BarsReceived = table.Column<int>(type: "INTEGER", nullable: false),
                    BarsInserted = table.Column<int>(type: "INTEGER", nullable: false),
                    DuplicateBars = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_HistoricalBackfillSegmentRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "HistoricalDataGapRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MissingBars = table.Column<int>(type: "INTEGER", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_HistoricalDataGapRecords", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillJobRecords_InstrumentId_Timeframe", table: "HistoricalBackfillJobRecords", columns: new[] { "InstrumentId", "Timeframe" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillJobRecords_NextAttemptUtc", table: "HistoricalBackfillJobRecords", column: "NextAttemptUtc");
            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillJobRecords_Status", table: "HistoricalBackfillJobRecords", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillJobRecords_UpdatedAtUtc", table: "HistoricalBackfillJobRecords", column: "UpdatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillSegmentRecords_InstrumentId", table: "HistoricalBackfillSegmentRecords", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillSegmentRecords_JobId_StartUtc_EndUtc", table: "HistoricalBackfillSegmentRecords", columns: new[] { "JobId", "StartUtc", "EndUtc" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillSegmentRecords_RequestedAtUtc", table: "HistoricalBackfillSegmentRecords", column: "RequestedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_HistoricalBackfillSegmentRecords_Status", table: "HistoricalBackfillSegmentRecords", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_HistoricalDataGapRecords_DetectedAtUtc", table: "HistoricalDataGapRecords", column: "DetectedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_HistoricalDataGapRecords_InstrumentId", table: "HistoricalDataGapRecords", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_HistoricalDataGapRecords_JobId_StartUtc_EndUtc", table: "HistoricalDataGapRecords", columns: new[] { "JobId", "StartUtc", "EndUtc" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HistoricalBackfillSegmentRecords");
            migrationBuilder.DropTable(name: "HistoricalDataGapRecords");
            migrationBuilder.DropTable(name: "HistoricalBackfillJobRecords");
        }
    }
}
