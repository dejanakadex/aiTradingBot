using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class ForexCooldownIndexTests
{
    [Fact]
    public async Task MigrationPreservesEvidenceAndCooldownUsesCoveringIndexWithoutSorting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TradingBotDbContext(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(db.Database.GetMigrations().Last(x => !x.EndsWith("AddForexCooldownIndex")));
        db.ResearchCandidateRecords.Add(new() { RecordKey = "preserved", InstrumentId = "EURUSD", StrategyId = "fx",
            Timeframe = Timeframe.FiveSeconds, DecisionStage = "FxShadowSignal", Outcome = ResearchCandidateOutcome.Accepted,
            EvaluatedAtUtc = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Equal("preserved", (await db.ResearchCandidateRecords.SingleAsync()).RecordKey);
        Assert.False(db.Database.HasPendingModelChanges());
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN SELECT EvaluatedAtUtc FROM ResearchCandidateRecords
            WHERE ReplayRunId IS NULL AND InstrumentId='EURUSD' AND StrategyId='fx' AND Timeframe=7
              AND DecisionStage='FxShadowSignal' AND Outcome=1 ORDER BY EvaluatedAtUtc DESC LIMIT 1
            """;
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        Assert.Contains(plan, x => x.Contains("COVERING INDEX IX_ResearchCandidate_FxCooldown", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, x => x.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }
}
