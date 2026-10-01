using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261001100000_AddTickCoverageGaps")]
public sealed class AddTickCoverageGaps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TickCoverageGapRecords",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                Symbol = table.Column<string>(type: "TEXT", nullable: false),
                StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                EndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: false),
                DroppedEvents = table.Column<long>(type: "INTEGER", nullable: false),
                RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_TickCoverageGapRecords", x => x.Id));
        migrationBuilder.CreateIndex("IX_TickCoverageGapRecords_InstrumentId_StartUtc", "TickCoverageGapRecords", new[] { "InstrumentId", "StartUtc" });
        migrationBuilder.CreateIndex("IX_TickCoverageGapRecords_RecordedAtUtc", "TickCoverageGapRecords", "RecordedAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("TickCoverageGapRecords");
}
