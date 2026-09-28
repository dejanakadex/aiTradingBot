using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260928230000_AddPortfolioRiskReservations")]
    public partial class AddPortfolioRiskReservations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PortfolioRiskReservationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReservationKey = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    PositionValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    RiskAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    SignedExposure = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CommittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BrokerOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_PortfolioRiskReservationRecords", x => x.Id));

            migrationBuilder.CreateTable(
                name: "PortfolioRiskReservationAuditRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    ReservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    ToStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_PortfolioRiskReservationAuditRecords", x => x.Id));

            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationRecords_ReservationKey", "PortfolioRiskReservationRecords", "ReservationKey", unique: true);
            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationRecords_AccountId", "PortfolioRiskReservationRecords", "AccountId");
            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationRecords_SignalId", "PortfolioRiskReservationRecords", "SignalId");
            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationRecords_Status_ExpiresAtUtc", "PortfolioRiskReservationRecords", new[] { "Status", "ExpiresAtUtc" });
            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationRecords_InstrumentId_Status", "PortfolioRiskReservationRecords", new[] { "InstrumentId", "Status" });
            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationRecords_StrategyId_Status", "PortfolioRiskReservationRecords", new[] { "StrategyId", "Status" });
            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationAuditRecords_ReservationId", "PortfolioRiskReservationAuditRecords", "ReservationId");
            migrationBuilder.CreateIndex("IX_PortfolioRiskReservationAuditRecords_TimestampUtc", "PortfolioRiskReservationAuditRecords", "TimestampUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PortfolioRiskReservationAuditRecords");
            migrationBuilder.DropTable(name: "PortfolioRiskReservationRecords");
        }
    }
}
