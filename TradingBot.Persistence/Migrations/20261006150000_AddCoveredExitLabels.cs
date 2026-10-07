using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261006150000_AddCoveredExitLabels")]
public sealed class AddCoveredExitLabels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>("HorizonExitPrice", "CandidateLabelRecords", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<decimal>("HorizonGrossReturnBps", "CandidateLabelRecords", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>("InputSha256", "CandidateLabelRecords", type: "TEXT", nullable: false, defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("HorizonExitPrice", "CandidateLabelRecords");
        migrationBuilder.DropColumn("HorizonGrossReturnBps", "CandidateLabelRecords");
        migrationBuilder.DropColumn("InputSha256", "CandidateLabelRecords");
    }
}
