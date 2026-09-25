using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class PatternEngineV2PersistenceTests
    {
        [Fact]
        public async Task MigrationPersistsCanonicalPatternIdentityWithoutTimeframeConflicts()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            await using var db = new TradingBotDbContext(options);
            await db.Database.MigrateAsync();
            var at = new DateTime(2026, 9, 25, 14, 0, 0, DateTimeKind.Utc);

            db.PatternDetections.AddRange(
                Record("US-STK-SPY-SMART|STRATEGY-A|LONG|HAMMER|ONEMINUTE|2026-09-25T14:00:00.0000000Z", Timeframe.OneMinute, at),
                Record("US-STK-SPY-SMART|STRATEGY-A|LONG|HAMMER|FIVEMINUTES|2026-09-25T14:00:00.0000000Z", Timeframe.FiveMinutes, at));
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.PatternDetections.CountAsync());
            Assert.All(await db.PatternDetections.ToArrayAsync(), item =>
            {
                Assert.Equal("US-STK-SPY-SMART", item.InstrumentId);
                Assert.Equal("strategy-a", item.StrategyId);
                Assert.Equal(TradeDirection.Long, item.Direction);
                Assert.StartsWith("patterns-v2+config-", item.PatternVersion);
            });

            db.PatternDetections.Add(Record(
                "US-STK-SPY-SMART|STRATEGY-A|LONG|HAMMER|ONEMINUTE|2026-09-25T14:00:00.0000000Z",
                Timeframe.OneMinute,
                at));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        private static PatternDetection Record(string patternKey, Timeframe timeframe, DateTime at) => new()
        {
            PatternKey = patternKey,
            SignalId = Guid.NewGuid(),
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            StrategyId = "strategy-a",
            PatternType = PatternType.Hammer,
            Timeframe = timeframe,
            Direction = TradeDirection.Long,
            PatternVersion = "patterns-v2+config-test",
            DetectedAtUtc = at,
            Details = "{}"
        };
    }
}
