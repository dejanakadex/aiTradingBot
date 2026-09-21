using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    public partial class AddAiApiUsageRecords : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiApiUsageRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestType = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CachedInputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiApiUsageRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_Pattern", table: "AiApiUsageRecords", column: "Pattern");
            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_RequestType", table: "AiApiUsageRecords", column: "RequestType");
            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_Success", table: "AiApiUsageRecords", column: "Success");
            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_Symbol", table: "AiApiUsageRecords", column: "Symbol");
            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_TimestampUtc", table: "AiApiUsageRecords", column: "TimestampUtc");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AiApiUsageRecords");
        }
    }
}
