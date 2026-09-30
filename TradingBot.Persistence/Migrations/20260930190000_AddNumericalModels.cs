using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations
{
    public partial class AddNumericalModels : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NumericalModelRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluationRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                    HorizonSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", nullable: false),
                    ModelVersionTag = table.Column<string>(type: "TEXT", nullable: false),
                    FeatureVersion = table.Column<string>(type: "TEXT", nullable: false),
                    PatternVersion = table.Column<string>(type: "TEXT", nullable: false),
                    LabelVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ProbabilityThreshold = table.Column<decimal>(type: "TEXT", nullable: false),
                    BaselineWalkForwardJson = table.Column<string>(type: "TEXT", nullable: false),
                    CandidateWalkForwardJson = table.Column<string>(type: "TEXT", nullable: false),
                    BaselineHoldoutJson = table.Column<string>(type: "TEXT", nullable: false),
                    CandidateHoldoutJson = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovalReady = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovalReasonsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModelArtifact = table.Column<byte[]>(type: "BLOB", nullable: false),
                    InputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    OutputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                    Reviewer = table.Column<string>(type: "TEXT", nullable: false),
                    DecisionReason = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_NumericalModelRecords", x => x.Id));

            migrationBuilder.CreateIndex("IX_NumericalModelRecords_CreatedAtUtc", "NumericalModelRecords", "CreatedAtUtc");
            migrationBuilder.CreateIndex("IX_NumericalModelRecords_EvaluationRunId", "NumericalModelRecords", "EvaluationRunId", unique: true);
            migrationBuilder.CreateIndex("IX_NumericalModelRecords_Status", "NumericalModelRecords", "Status");
            migrationBuilder.CreateIndex("IX_NumericalModelRecords_InstrumentId_StrategyId_ModelVersion", "NumericalModelRecords", new[] { "InstrumentId", "StrategyId", "ModelVersion" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "NumericalModelRecords");
    }
}
