using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261006140000_AddReplayBacktest")]
public sealed class AddReplayBacktest : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ReplayBacktestRecords",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                ReplayRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                InputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                RequestSha256 = table.Column<string>(type: "TEXT", nullable: false),
                OutputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                ResultsJson = table.Column<string>(type: "TEXT", nullable: false),
                SignalCount = table.Column<int>(type: "INTEGER", nullable: false),
                FilledTradeCount = table.Column<int>(type: "INTEGER", nullable: false),
                ClosedTradeCount = table.Column<int>(type: "INTEGER", nullable: false),
                RealizedNetPnl = table.Column<decimal>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ReplayBacktestRecords", x => x.Id));
        migrationBuilder.CreateIndex(
            name: "IX_ReplayBacktestRecords_ReplayRunId_RequestSha256",
            table: "ReplayBacktestRecords",
            columns: new[] { "ReplayRunId", "RequestSha256" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ReplayBacktestRecords");
}
