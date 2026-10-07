using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261007100000_AddNumericalModelDecisionAudit")]
public sealed class AddNumericalModelDecisionAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The historical AddNumericalModels class lacks EF migration metadata in existing checkouts.
        // Preserve an already populated table while making a fresh migrated database operational.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "NumericalModelRecords" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_NumericalModelRecords" PRIMARY KEY,
                "ModelVersion" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "EvaluationRunId" TEXT NOT NULL,
                "InstrumentId" TEXT NOT NULL,
                "StrategyId" TEXT NOT NULL,
                "HorizonSeconds" INTEGER NOT NULL,
                "Algorithm" TEXT NOT NULL,
                "ModelVersionTag" TEXT NOT NULL,
                "FeatureVersion" TEXT NOT NULL,
                "PatternVersion" TEXT NOT NULL,
                "LabelVersion" TEXT NOT NULL,
                "ProbabilityThreshold" TEXT NOT NULL,
                "BaselineWalkForwardJson" TEXT NOT NULL,
                "CandidateWalkForwardJson" TEXT NOT NULL,
                "BaselineHoldoutJson" TEXT NOT NULL,
                "CandidateHoldoutJson" TEXT NOT NULL,
                "ApprovalReady" INTEGER NOT NULL,
                "ApprovalReasonsJson" TEXT NOT NULL,
                "ModelArtifact" BLOB NOT NULL,
                "InputSha256" TEXT NOT NULL,
                "OutputSha256" TEXT NOT NULL,
                "Reviewer" TEXT NOT NULL,
                "DecisionReason" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "DecidedAtUtc" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_NumericalModelRecords_CreatedAtUtc" ON "NumericalModelRecords" ("CreatedAtUtc");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_NumericalModelRecords_EvaluationRunId" ON "NumericalModelRecords" ("EvaluationRunId");
            CREATE INDEX IF NOT EXISTS "IX_NumericalModelRecords_Status" ON "NumericalModelRecords" ("Status");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_NumericalModelRecords_InstrumentId_StrategyId_ModelVersion"
                ON "NumericalModelRecords" ("InstrumentId", "StrategyId", "ModelVersion");
            """);
        migrationBuilder.CreateTable(
            name: "NumericalModelDecisionRecords",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ModelId = table.Column<Guid>(type: "TEXT", nullable: false),
                InstrumentId = table.Column<string>(type: "TEXT", nullable: false),
                StrategyId = table.Column<string>(type: "TEXT", nullable: false),
                StatusBefore = table.Column<int>(type: "INTEGER", nullable: false),
                StatusAfter = table.Column<int>(type: "INTEGER", nullable: false),
                Action = table.Column<string>(type: "TEXT", nullable: false),
                Reviewer = table.Column<string>(type: "TEXT", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: false),
                ReviewedOutputSha256 = table.Column<string>(type: "TEXT", nullable: false),
                DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_NumericalModelDecisionRecords", x => x.Id));
        migrationBuilder.CreateIndex("IX_NumericalModelDecisionRecords_ModelId_DecidedAtUtc",
            "NumericalModelDecisionRecords", new[] { "ModelId", "DecidedAtUtc" });
        migrationBuilder.CreateIndex("IX_NumericalModelDecisionRecords_InstrumentId_StrategyId_DecidedAtUtc",
            "NumericalModelDecisionRecords", new[] { "InstrumentId", "StrategyId", "DecidedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("NumericalModelDecisionRecords");
}
