using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260929010000_AddSignalArbitration")]
    public partial class AddSignalArbitration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Some supported legacy databases have the exit-management migration in
            // __EFMigrationsHistory while the table itself is missing. Repair that
            // known drift before adding the attribution columns so the migration is
            // safe for both clean databases and existing installations.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "ExitManagementRecords" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_ExitManagementRecords" PRIMARY KEY AUTOINCREMENT,
                    "TradeId" INTEGER NULL,
                    "Symbol" TEXT NOT NULL,
                    "EntryBrokerOrderId" TEXT NOT NULL,
                    "ProtectiveStopBrokerOrderId" TEXT NOT NULL,
                    "State" INTEGER NOT NULL,
                    "InitialEntryPrice" TEXT NOT NULL,
                    "InitialStopPrice" TEXT NOT NULL,
                    "InitialRiskPerShare" TEXT NOT NULL,
                    "FilledQuantity" TEXT NOT NULL,
                    "ProtectedQuantity" TEXT NOT NULL,
                    "HighestPriceSinceEntry" TEXT NOT NULL,
                    "CurrentProtectiveStop" TEXT NOT NULL,
                    "BreakEvenActivated" INTEGER NOT NULL,
                    "TrailingActivated" INTEGER NOT NULL,
                    "TrailingAtrTimeframe" TEXT NOT NULL,
                    "TrailingAtrMultiplier" TEXT NOT NULL,
                    "OpenedUtc" TEXT NOT NULL,
                    "UpdatedUtc" TEXT NOT NULL,
                    "ClosedUtc" TEXT NULL,
                    "RawJson" TEXT NOT NULL
                );
                """);

            migrationBuilder.AddColumn<Guid>(name: "SignalId", table: "ExitManagementRecords", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "InstrumentId", table: "ExitManagementRecords", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>(name: "StrategyId", table: "ExitManagementRecords", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.CreateIndex(name: "IX_ExitManagementRecords_SignalId", table: "ExitManagementRecords", column: "SignalId");

            migrationBuilder.CreateTable(
                name: "SignalArbitrationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArbitrationKey = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApprovedQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    PositionValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    FilledQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExitedQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryBrokerOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_SignalArbitrationRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "SignalArbitrationAuditRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    ArbitrationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ToStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_SignalArbitrationAuditRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "VirtualAllocationOrderRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    ArbitrationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BrokerOrderId = table.Column<string>(type: "TEXT", nullable: false),
                    IsEntry = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestedQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    FilledQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_VirtualAllocationOrderRecords", x => x.Id));

            migrationBuilder.CreateIndex("IX_SignalArbitrationRecords_ArbitrationKey", "SignalArbitrationRecords", "ArbitrationKey", unique: true);
            migrationBuilder.CreateIndex("IX_SignalArbitrationRecords_SignalId", "SignalArbitrationRecords", "SignalId", unique: true);
            migrationBuilder.CreateIndex("IX_SignalArbitrationRecords_AccountId", "SignalArbitrationRecords", "AccountId");
            migrationBuilder.CreateIndex("IX_SignalArbitrationRecords_InstrumentId_Status", "SignalArbitrationRecords", new[] { "InstrumentId", "Status" });
            migrationBuilder.CreateIndex("IX_SignalArbitrationRecords_StrategyId_Status", "SignalArbitrationRecords", new[] { "StrategyId", "Status" });
            migrationBuilder.CreateIndex("IX_SignalArbitrationRecords_Status_ExpiresAtUtc", "SignalArbitrationRecords", new[] { "Status", "ExpiresAtUtc" });
            migrationBuilder.CreateIndex("IX_SignalArbitrationAuditRecords_ArbitrationId", "SignalArbitrationAuditRecords", "ArbitrationId");
            migrationBuilder.CreateIndex("IX_SignalArbitrationAuditRecords_TimestampUtc", "SignalArbitrationAuditRecords", "TimestampUtc");
            migrationBuilder.CreateIndex("IX_VirtualAllocationOrderRecords_BrokerOrderId", "VirtualAllocationOrderRecords", "BrokerOrderId", unique: true);
            migrationBuilder.CreateIndex("IX_VirtualAllocationOrderRecords_ArbitrationId_IsEntry", "VirtualAllocationOrderRecords", new[] { "ArbitrationId", "IsEntry" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SignalArbitrationAuditRecords");
            migrationBuilder.DropTable(name: "SignalArbitrationRecords");
            migrationBuilder.DropTable(name: "VirtualAllocationOrderRecords");
            migrationBuilder.DropIndex(name: "IX_ExitManagementRecords_SignalId", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "SignalId", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "InstrumentId", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "StrategyId", table: "ExitManagementRecords");
        }
    }
}
