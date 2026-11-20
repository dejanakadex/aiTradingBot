using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TradingBot.Persistence;

/// <summary>Slow command attribution only. Never log SQL text, parameters, connection strings or account data.</summary>
public sealed class SlowDatabaseCommandInterceptor(ILogger<SlowDatabaseCommandInterceptor> logger) : DbCommandInterceptor
{
    private static readonly Regex Table = new("(?:UPDATE|INTO|FROM)\\s+\"([A-Za-z0-9_]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    private void Observe(DbCommand command, CommandExecutedEventData data)
    {
        if (data.Duration.TotalMilliseconds < 250) return;
        var sql = command.CommandText.TrimStart();
        var operation = new[] { "SELECT", "UPDATE", "INSERT", "DELETE", "PRAGMA" }.FirstOrDefault(x => sql.StartsWith(x + " ", StringComparison.OrdinalIgnoreCase)) ?? "OTHER";
        string table;
        try { var match = Table.Match(sql); table = match.Success ? match.Groups[1].Value : "unknown"; }
        catch (RegexMatchTimeoutException) { table = "unknown"; }
        logger.LogWarning("Slow database command AsOfUtc={AsOfUtc:O} Milliseconds={Milliseconds} Operation={Operation} Table={Table} Context={Context} Source={Source}",
            DateTime.UtcNow, data.Duration.TotalMilliseconds, operation, table, data.Context?.ContextId.InstanceId, data.CommandSource);
    }
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { Observe(command, eventData); return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    { Observe(command, eventData); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { Observe(command, eventData); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { Observe(command, eventData); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    { Observe(command, eventData); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    { Observe(command, eventData); return ValueTask.FromResult(result); }
}

// Command timing excludes transaction acquisition/commit. Keep those waits separately attributable.
public sealed class SlowDatabaseTransactionInterceptor(ILogger<SlowDatabaseTransactionInterceptor> logger) : DbTransactionInterceptor
{
    private void Observe(string phase, TransactionEndEventData data)
    {
        if (data.Duration.TotalMilliseconds >= 250)
            logger.LogWarning("Slow database transaction AsOfUtc={AsOfUtc:O} Milliseconds={Milliseconds} Phase={Phase} Context={Context}",
                DateTime.UtcNow, data.Duration.TotalMilliseconds, phase, data.Context?.ContextId.InstanceId);
    }
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    { Observe("Begin", eventData); return result; }
    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    { Observe("Begin", eventData); return ValueTask.FromResult(result); }
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Observe("Commit", eventData);
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { Observe("Commit", eventData); return Task.CompletedTask; }
}
