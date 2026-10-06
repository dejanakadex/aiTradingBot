using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20261006120000_AddFixedBracketExitState")]
    public partial class AddFixedBracketExitState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CurrentTakeProfitPrice",
                table: "ExitManagementRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
            migrationBuilder.AddColumn<DateTime>(
                name: "ProtectiveStopCancelConfirmedUtc",
                table: "ExitManagementRecords",
                type: "TEXT",
                nullable: true);
            migrationBuilder.AddColumn<DateTime>(
                name: "TakeProfitCancelConfirmedUtc",
                table: "ExitManagementRecords",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CurrentTakeProfitPrice", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "ProtectiveStopCancelConfirmedUtc", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "TakeProfitCancelConfirmedUtc", table: "ExitManagementRecords");
        }
    }
}
