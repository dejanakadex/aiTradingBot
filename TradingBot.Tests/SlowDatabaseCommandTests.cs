using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class SlowDatabaseCommandTests
{
    [Fact]
    public async Task SlowCommandKeepsResultAndLogsAttributionWithoutSqlOrParameterValues()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        connection.CreateFunction("slow_for_test", (string _) => { Thread.Sleep(300); return 42; });
        var log = new CapturedLog();
        var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection)
            .AddInterceptors(new SlowDatabaseCommandInterceptor(log)).Options;
        await using var db = new TradingBotDbContext(options);
        var result = await db.Database.SqlQueryRaw<int>("SELECT slow_for_test({0}) AS Value", "private-test-value").SingleAsync();
        Assert.Equal(42, result);
        Assert.NotEmpty(log.Messages);
        Assert.All(log.Messages, message =>
        {
            Assert.Contains("Operation=SELECT", message);
            Assert.DoesNotContain("private-test-value", message);
            Assert.DoesNotContain("slow_for_test", message);
        });
    }
    [Fact]
    public async Task ReaderLifetimeFindsDelayAfterFastFirstRow()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        connection.CreateFunction("slow_next_row", (int value) => { if (value == 2) Thread.Sleep(300); return value; });
        var log = new CapturedLog();
        await using var db = new TradingBotDbContext(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection)
            .AddInterceptors(new SlowDatabaseCommandInterceptor(log)).Options);
        var rows = await db.Database.SqlQueryRaw<int>("SELECT slow_next_row(1) AS Value UNION ALL SELECT slow_next_row(2)").ToArrayAsync();
        Assert.Equal(new[] { 1, 2 }, rows);
        Assert.Contains(log.Messages, x => x.Contains("Phase=ReaderLifetime"));
        Assert.DoesNotContain(log.Messages, x => x.Contains("Phase=Execute"));
    }
    private sealed class CapturedLog : ILogger<SlowDatabaseCommandInterceptor>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
