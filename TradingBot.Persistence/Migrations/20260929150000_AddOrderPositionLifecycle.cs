using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    [DbContext(typeof(TradingBotDbContext))]
    [Migration("20260929150000_AddOrderPositionLifecycle")]
    public partial class AddOrderPositionLifecycle : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>("IntentId", "OrderRecords", "TEXT", nullable: false, defaultValue: Guid.Empty);
            migrationBuilder.AddColumn<string>("Role", "OrderRecords", "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>("ParentBrokerOrderId", "OrderRecords", "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<DateTime>("UpdatedUtc", "OrderRecords", "TEXT", nullable: false, defaultValue: DateTime.UnixEpoch);
            migrationBuilder.AddColumn<string>("Side", "OrderRecords", "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>("OrderType", "OrderRecords", "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<decimal>("RequestedQuantity", "OrderRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("FilledQuantity", "OrderRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("RemainingQuantity", "OrderRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("LimitPrice", "OrderRecords", "TEXT", nullable: true);
            migrationBuilder.AddColumn<decimal>("StopPrice", "OrderRecords", "TEXT", nullable: true);
            migrationBuilder.AddColumn<decimal>("AverageFillPrice", "OrderRecords", "TEXT", nullable: true);
            migrationBuilder.AddColumn<decimal>("TotalCommission", "OrderRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<DateTime>("CancelRequestedUtc", "OrderRecords", "TEXT", nullable: true);
            migrationBuilder.AddColumn<DateTime>("CancelConfirmedUtc", "OrderRecords", "TEXT", nullable: true);

            migrationBuilder.AddColumn<decimal>("Quantity", "ExecutionRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("Price", "ExecutionRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("Commission", "ExecutionRecords", "TEXT", nullable: true);
            migrationBuilder.AddColumn<DateTime>("CommissionUpdatedUtc", "ExecutionRecords", "TEXT", nullable: true);

            migrationBuilder.AddColumn<string>("TakeProfitBrokerOrderId", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>("ExitBrokerOrderId", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<string>("ExitReason", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<decimal>("ExitRequestedQuantity", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("ExitedQuantity", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("ProtectiveStopFilledQuantity", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("TakeProfitFilledQuantity", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>("ManagedExitFilledQuantity", "ExitManagementRecords", "TEXT", nullable: false, defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "TradingControlStateRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_TradingControlStateRecords", x => x.Id));

            migrationBuilder.Sql("UPDATE OrderRecords SET IntentId = lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1,1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))), UpdatedUtc = CreatedUtc;");
            migrationBuilder.Sql(
                """
                UPDATE ExecutionRecords
                SET OrderRecordId = (
                    SELECT keeper.Id
                    FROM OrderRecords source
                    JOIN OrderRecords keeper ON keeper.BrokerOrderId = source.BrokerOrderId
                    WHERE source.Id = ExecutionRecords.OrderRecordId
                    ORDER BY keeper.CreatedUtc DESC, keeper.Id DESC
                    LIMIT 1)
                WHERE OrderRecordId IN (
                    SELECT duplicate.Id
                    FROM OrderRecords duplicate
                    WHERE duplicate.BrokerOrderId <> ''
                      AND EXISTS (
                          SELECT 1 FROM OrderRecords newer
                          WHERE newer.BrokerOrderId = duplicate.BrokerOrderId
                            AND (newer.CreatedUtc > duplicate.CreatedUtc
                              OR (newer.CreatedUtc = duplicate.CreatedUtc AND newer.Id > duplicate.Id))));

                DELETE FROM OrderRecords
                WHERE BrokerOrderId <> ''
                  AND EXISTS (
                      SELECT 1 FROM OrderRecords newer
                      WHERE newer.BrokerOrderId = OrderRecords.BrokerOrderId
                        AND (newer.CreatedUtc > OrderRecords.CreatedUtc
                          OR (newer.CreatedUtc = OrderRecords.CreatedUtc AND newer.Id > OrderRecords.Id)));
                """);

            migrationBuilder.DropIndex("IX_OrderRecords_BrokerOrderId", "OrderRecords");
            migrationBuilder.CreateIndex("IX_OrderRecords_BrokerOrderId", "OrderRecords", "BrokerOrderId", unique: true, filter: "BrokerOrderId <> ''");
            migrationBuilder.CreateIndex("IX_OrderRecords_IntentId", "OrderRecords", "IntentId", unique: true, filter: "IntentId <> '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.CreateIndex("IX_OrderRecords_Status", "OrderRecords", "Status");
            migrationBuilder.CreateIndex("IX_OrderRecords_UpdatedUtc", "OrderRecords", "UpdatedUtc");
            migrationBuilder.CreateIndex("IX_ExecutionRecords_OrderRecordId", "ExecutionRecords", "OrderRecordId");
            migrationBuilder.CreateIndex("IX_ExitManagementRecords_TakeProfitBrokerOrderId", "ExitManagementRecords", "TakeProfitBrokerOrderId");
            migrationBuilder.CreateIndex("IX_ExitManagementRecords_ExitBrokerOrderId", "ExitManagementRecords", "ExitBrokerOrderId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("TradingControlStateRecords");
            migrationBuilder.DropIndex("IX_OrderRecords_BrokerOrderId", "OrderRecords");
            migrationBuilder.DropIndex("IX_OrderRecords_IntentId", "OrderRecords");
            migrationBuilder.DropIndex("IX_OrderRecords_Status", "OrderRecords");
            migrationBuilder.DropIndex("IX_OrderRecords_UpdatedUtc", "OrderRecords");
            migrationBuilder.DropIndex("IX_ExecutionRecords_OrderRecordId", "ExecutionRecords");
            migrationBuilder.DropIndex("IX_ExitManagementRecords_TakeProfitBrokerOrderId", "ExitManagementRecords");
            migrationBuilder.DropIndex("IX_ExitManagementRecords_ExitBrokerOrderId", "ExitManagementRecords");
            migrationBuilder.CreateIndex("IX_OrderRecords_BrokerOrderId", "OrderRecords", "BrokerOrderId");

            foreach (var column in new[] { "IntentId", "Role", "ParentBrokerOrderId", "UpdatedUtc", "Side", "OrderType", "RequestedQuantity", "FilledQuantity", "RemainingQuantity", "LimitPrice", "StopPrice", "AverageFillPrice", "TotalCommission", "CancelRequestedUtc", "CancelConfirmedUtc" })
                migrationBuilder.DropColumn(column, "OrderRecords");
            foreach (var column in new[] { "Quantity", "Price", "Commission", "CommissionUpdatedUtc" })
                migrationBuilder.DropColumn(column, "ExecutionRecords");
            foreach (var column in new[] { "TakeProfitBrokerOrderId", "ExitBrokerOrderId", "ExitReason", "ExitRequestedQuantity", "ExitedQuantity", "ProtectiveStopFilledQuantity", "TakeProfitFilledQuantity", "ManagedExitFilledQuantity" })
                migrationBuilder.DropColumn(column, "ExitManagementRecords");
        }
    }
}
