using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    public partial class AddOrderClientOrderKey : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientOrderKey",
                table: "OrderRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_OrderRecords_ClientOrderKey",
                table: "OrderRecords",
                column: "ClientOrderKey",
                unique: true,
                filter: "ClientOrderKey <> ''");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionRecords_BrokerExecutionId",
                table: "ExecutionRecords");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionRecords_BrokerExecutionId",
                table: "ExecutionRecords",
                column: "BrokerExecutionId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderRecords_ClientOrderKey",
                table: "OrderRecords");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionRecords_BrokerExecutionId",
                table: "ExecutionRecords");

            migrationBuilder.DropColumn(
                name: "ClientOrderKey",
                table: "OrderRecords");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionRecords_BrokerExecutionId",
                table: "ExecutionRecords",
                column: "BrokerExecutionId");
        }
    }
}
