using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260923194000_AddCanonicalMarketData")]
    public partial class AddCanonicalMarketData : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "InstrumentId", table: "Candles", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<bool>(name: "IsFinal", table: "Candles", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<int>(name: "QualityStatus", table: "Candles", type: "INTEGER", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<DateTime>(name: "ReceivedTimeUtc", table: "Candles", type: "TEXT", nullable: false, defaultValue: new DateTime(1, 1, 1));
            migrationBuilder.AddColumn<string>(name: "Source", table: "Candles", type: "TEXT", nullable: false, defaultValue: "");

            migrationBuilder.Sql("UPDATE Candles SET InstrumentId = Symbol, ReceivedTimeUtc = TimestampUtc, Source = 'Legacy', IsFinal = 1, QualityStatus = 0;");

            migrationBuilder.CreateTable(
                name: "MarketDataQualityIncidentRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    StreamKey = table.Column<string>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    EventTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_MarketDataQualityIncidentRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "MarketDataStreamStateRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    StreamKey = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    LastEventId = table.Column<string>(type: "TEXT", nullable: false),
                    LastEventTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReceivedTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusReason = table.Column<string>(type: "TEXT", nullable: false),
                    IsHealthy = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_MarketDataStreamStateRecords", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_Candles_InstrumentId", table: "Candles", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_Candles_QualityStatus", table: "Candles", column: "QualityStatus");
            migrationBuilder.CreateIndex(name: "IX_Candles_ReceivedTimeUtc", table: "Candles", column: "ReceivedTimeUtc");
            migrationBuilder.CreateIndex(name: "IX_MarketDataQualityIncidentRecords_EventId", table: "MarketDataQualityIncidentRecords", column: "EventId");
            migrationBuilder.CreateIndex(name: "IX_MarketDataQualityIncidentRecords_InstrumentId", table: "MarketDataQualityIncidentRecords", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_MarketDataQualityIncidentRecords_RecordedAtUtc", table: "MarketDataQualityIncidentRecords", column: "RecordedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_MarketDataQualityIncidentRecords_Status", table: "MarketDataQualityIncidentRecords", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_MarketDataQualityIncidentRecords_StreamKey", table: "MarketDataQualityIncidentRecords", column: "StreamKey");
            migrationBuilder.CreateIndex(name: "IX_MarketDataStreamStateRecords_InstrumentId", table: "MarketDataStreamStateRecords", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_MarketDataStreamStateRecords_Status", table: "MarketDataStreamStateRecords", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_MarketDataStreamStateRecords_StreamKey", table: "MarketDataStreamStateRecords", column: "StreamKey", unique: true);
            migrationBuilder.CreateIndex(name: "IX_MarketDataStreamStateRecords_UpdatedAtUtc", table: "MarketDataStreamStateRecords", column: "UpdatedAtUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MarketDataQualityIncidentRecords");
            migrationBuilder.DropTable(name: "MarketDataStreamStateRecords");
            migrationBuilder.DropIndex(name: "IX_Candles_InstrumentId", table: "Candles");
            migrationBuilder.DropIndex(name: "IX_Candles_QualityStatus", table: "Candles");
            migrationBuilder.DropIndex(name: "IX_Candles_ReceivedTimeUtc", table: "Candles");
            migrationBuilder.DropColumn(name: "InstrumentId", table: "Candles");
            migrationBuilder.DropColumn(name: "IsFinal", table: "Candles");
            migrationBuilder.DropColumn(name: "QualityStatus", table: "Candles");
            migrationBuilder.DropColumn(name: "ReceivedTimeUtc", table: "Candles");
            migrationBuilder.DropColumn(name: "Source", table: "Candles");
        }
    }
}
