using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261025180000_AddForexExecutionProofs")]
public sealed class AddForexExecutionProofs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("ForexExecutionProofRecords", table => new
        {
            ReservationId = table.Column<Guid>(type: "TEXT", nullable: false),
            InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
            CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            RequestJson = table.Column<string>(type: "TEXT", nullable: false),
            AssessmentJson = table.Column<string>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ForexExecutionProofRecords", x => x.ReservationId));
        migrationBuilder.CreateIndex("IX_ForexExecutionProofRecords_InstrumentId", "ForexExecutionProofRecords", "InstrumentId");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ForexExecutionProofRecords");
}
