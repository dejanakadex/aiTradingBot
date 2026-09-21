using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    public partial class AddAiAgentMetadata : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentType",
                table: "AiAnalysisRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "AiAnalysisRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                table: "AiAnalysisRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentType",
                table: "AiApiUsageRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                table: "AiApiUsageRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(name: "IX_AiAnalysisRecords_AgentType", table: "AiAnalysisRecords", column: "AgentType");
            migrationBuilder.CreateIndex(name: "IX_AiAnalysisRecords_Model", table: "AiAnalysisRecords", column: "Model");
            migrationBuilder.CreateIndex(name: "IX_AiAnalysisRecords_PromptVersion", table: "AiAnalysisRecords", column: "PromptVersion");
            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_AgentType", table: "AiApiUsageRecords", column: "AgentType");
            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_Model", table: "AiApiUsageRecords", column: "Model");
            migrationBuilder.CreateIndex(name: "IX_AiApiUsageRecords_PromptVersion", table: "AiApiUsageRecords", column: "PromptVersion");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_AiAnalysisRecords_AgentType", table: "AiAnalysisRecords");
            migrationBuilder.DropIndex(name: "IX_AiAnalysisRecords_Model", table: "AiAnalysisRecords");
            migrationBuilder.DropIndex(name: "IX_AiAnalysisRecords_PromptVersion", table: "AiAnalysisRecords");
            migrationBuilder.DropIndex(name: "IX_AiApiUsageRecords_AgentType", table: "AiApiUsageRecords");
            migrationBuilder.DropIndex(name: "IX_AiApiUsageRecords_Model", table: "AiApiUsageRecords");
            migrationBuilder.DropIndex(name: "IX_AiApiUsageRecords_PromptVersion", table: "AiApiUsageRecords");

            migrationBuilder.DropColumn(name: "AgentType", table: "AiAnalysisRecords");
            migrationBuilder.DropColumn(name: "Model", table: "AiAnalysisRecords");
            migrationBuilder.DropColumn(name: "PromptVersion", table: "AiAnalysisRecords");
            migrationBuilder.DropColumn(name: "AgentType", table: "AiApiUsageRecords");
            migrationBuilder.DropColumn(name: "PromptVersion", table: "AiApiUsageRecords");
        }
    }
}
