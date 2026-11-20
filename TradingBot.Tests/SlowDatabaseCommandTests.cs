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
        var message = Assert.Single(log.Messages);
        Assert.Contains("Operation=SELECT", message);
        Assert.DoesNotContain("private-test-value", message);
        Assert.DoesNotContain("slow_for_test", message);
    }
    private sealed class CapturedLog : ILogger<SlowDatabaseCommandInterceptor>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
