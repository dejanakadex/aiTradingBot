using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    public partial class AddExitManagementRecords : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExitManagementRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    EntryBrokerOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectiveStopBrokerOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    InitialEntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    InitialStopPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    InitialRiskPerShare = table.Column<decimal>(type: "TEXT", nullable: false),
                    FilledQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    ProtectedQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    HighestPriceSinceEntry = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentProtectiveStop = table.Column<decimal>(type: "TEXT", nullable: false),
                    BreakEvenActivated = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrailingActivated = table.Column<bool>(type: "INTEGER", nullable: false),
                    TrailingAtrTimeframe = table.Column<string>(type: "TEXT", nullable: false),
                    TrailingAtrMultiplier = table.Column<decimal>(type: "TEXT", nullable: false),
                    OpenedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExitManagementRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExitStopAuditRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TradeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    EntryBrokerOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    BrokerOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    OldStop = table.Column<decimal>(type: "TEXT", nullable: true),
                    NewStop = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    MarketPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    HighestPriceSinceEntry = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentAtr = table.Column<decimal>(type: "TEXT", nullable: true),
                    CurrentRMultiple = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExitManagementState = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExitStopAuditRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(name: "IX_ExitManagementRecords_EntryBrokerOrderId", table: "ExitManagementRecords", column: "EntryBrokerOrderId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ExitManagementRecords_ProtectiveStopBrokerOrderId", table: "ExitManagementRecords", column: "ProtectiveStopBrokerOrderId");
            migrationBuilder.CreateIndex(name: "IX_ExitManagementRecords_State", table: "ExitManagementRecords", column: "State");
            migrationBuilder.CreateIndex(name: "IX_ExitManagementRecords_Symbol", table: "ExitManagementRecords", column: "Symbol");
            migrationBuilder.CreateIndex(name: "IX_ExitManagementRecords_UpdatedUtc", table: "ExitManagementRecords", column: "UpdatedUtc");
            migrationBuilder.CreateIndex(name: "IX_ExitStopAuditRecords_BrokerOrderId", table: "ExitStopAuditRecords", column: "BrokerOrderId");
            migrationBuilder.CreateIndex(name: "IX_ExitStopAuditRecords_EntryBrokerOrderId", table: "ExitStopAuditRecords", column: "EntryBrokerOrderId");
            migrationBuilder.CreateIndex(name: "IX_ExitStopAuditRecords_Symbol", table: "ExitStopAuditRecords", column: "Symbol");
            migrationBuilder.CreateIndex(name: "IX_ExitStopAuditRecords_TimestampUtc", table: "ExitStopAuditRecords", column: "TimestampUtc");
            migrationBuilder.CreateIndex(name: "IX_ExitStopAuditRecords_TradeId", table: "ExitStopAuditRecords", column: "TradeId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ExitManagementRecords");
            migrationBuilder.DropTable(name: "ExitStopAuditRecords");
        }
    }
}
