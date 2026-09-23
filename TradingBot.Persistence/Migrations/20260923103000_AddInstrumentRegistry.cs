using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260923103000_AddInstrumentRegistry")]
    public partial class AddInstrumentRegistry : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstrumentRegistryRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Exchange = table.Column<string>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityType = table.Column<string>(type: "TEXT", nullable: false),
                    ConfiguredEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TradingRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowedDirectionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    MarketDataTimeframesJson = table.Column<string>(type: "TEXT", nullable: false),
                    MaximumPositionValue = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaximumHoldingSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    BrokerContractId = table.Column<long>(type: "INTEGER", nullable: true),
                    BrokerPrimaryExchange = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationHash = table.Column<string>(type: "TEXT", nullable: false),
                    StatusReason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StatusChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentRegistryRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InstrumentStatusTransitionRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    FromStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ToStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentStatusTransitionRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(name: "IX_InstrumentRegistryRecords_ConfiguredEnabled", table: "InstrumentRegistryRecords", column: "ConfiguredEnabled");
            migrationBuilder.CreateIndex(name: "IX_InstrumentRegistryRecords_InstrumentId", table: "InstrumentRegistryRecords", column: "InstrumentId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_InstrumentRegistryRecords_Status", table: "InstrumentRegistryRecords", column: "Status");
            migrationBuilder.CreateIndex(name: "IX_InstrumentRegistryRecords_Symbol", table: "InstrumentRegistryRecords", column: "Symbol");
            migrationBuilder.CreateIndex(name: "IX_InstrumentRegistryRecords_UpdatedAtUtc", table: "InstrumentRegistryRecords", column: "UpdatedAtUtc");
            migrationBuilder.CreateIndex(name: "IX_InstrumentStatusTransitionRecords_InstrumentId", table: "InstrumentStatusTransitionRecords", column: "InstrumentId");
            migrationBuilder.CreateIndex(name: "IX_InstrumentStatusTransitionRecords_TimestampUtc", table: "InstrumentStatusTransitionRecords", column: "TimestampUtc");
            migrationBuilder.CreateIndex(name: "IX_InstrumentStatusTransitionRecords_ToStatus", table: "InstrumentStatusTransitionRecords", column: "ToStatus");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "InstrumentRegistryRecords");
            migrationBuilder.DropTable(name: "InstrumentStatusTransitionRecords");
        }
    }
}
