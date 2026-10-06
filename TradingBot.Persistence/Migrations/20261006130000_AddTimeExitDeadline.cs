using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20261006130000_AddTimeExitDeadline")]
    public partial class AddTimeExitDeadline : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(name: "ExitDeadlineUtc", table: "ExitManagementRecords", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<decimal>(name: "EntryRemainingQuantity", table: "ExitManagementRecords", type: "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<DateTime>(name: "EntryCancelConfirmedUtc", table: "ExitManagementRecords", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "TimeExitDeferredUtc", table: "ExitManagementRecords", type: "TEXT", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TimeExitDeferredReason", table: "ExitManagementRecords", type: "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.CreateIndex(name: "IX_ExitManagementRecords_ExitDeadlineUtc", table: "ExitManagementRecords", column: "ExitDeadlineUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_ExitManagementRecords_ExitDeadlineUtc", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "ExitDeadlineUtc", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "EntryRemainingQuantity", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "EntryCancelConfirmedUtc", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "TimeExitDeferredUtc", table: "ExitManagementRecords");
            migrationBuilder.DropColumn(name: "TimeExitDeferredReason", table: "ExitManagementRecords");
        }
    }
}
