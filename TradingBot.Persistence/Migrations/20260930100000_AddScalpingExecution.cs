using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260930100000_AddScalpingExecution")]
    public partial class AddScalpingExecution : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaximumHoldingSeconds",
                table: "ExitManagementRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScalpingExecutionDecisionRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SignalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    EvaluatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QuoteAsOfUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    SelectedPolicy = table.Column<string>(type: "TEXT", nullable: false),
                    Bid = table.Column<decimal>(type: "TEXT", nullable: false),
                    Ask = table.Column<decimal>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExpectedHoldingSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedGrossEdgeBps = table.Column<decimal>(type: "TEXT", nullable: false),
                    EstimatedCostBps = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExpectedNetEdgeBps = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    DecisionJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ScalpingExecutionDecisionRecords", x => x.Id));

            migrationBuilder.CreateIndex("IX_ScalpingExecutionDecisionRecords_Approved", "ScalpingExecutionDecisionRecords", "Approved");
            migrationBuilder.CreateIndex("IX_ScalpingExecutionDecisionRecords_EvaluatedAtUtc", "ScalpingExecutionDecisionRecords", "EvaluatedAtUtc");
            migrationBuilder.CreateIndex("IX_ScalpingExecutionDecisionRecords_InstrumentId", "ScalpingExecutionDecisionRecords", "InstrumentId");
            migrationBuilder.CreateIndex("IX_ScalpingExecutionDecisionRecords_SignalId", "ScalpingExecutionDecisionRecords", "SignalId");
            migrationBuilder.CreateIndex("IX_ScalpingExecutionDecisionRecords_Symbol", "ScalpingExecutionDecisionRecords", "Symbol");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("ScalpingExecutionDecisionRecords");
            migrationBuilder.DropColumn("MaximumHoldingSeconds", "ExitManagementRecords");
        }
    }
}
