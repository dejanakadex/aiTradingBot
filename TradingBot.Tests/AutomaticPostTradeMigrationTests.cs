using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class AutomaticPostTradeMigrationTests
{
    [Fact]
    public async Task UpgradePreservesLegacyDuplicateSourceRowsAndEnforcesAutomaticIdentity()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        using var db = new TradingBotDbContext(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261119220000_AddForexCooldownIndex");
        var fields = new List<string>(); var values = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(PostTradeAnalysisRecords)";
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(1); if (name == "Id") continue;
                fields.Add('"' + name + '"');
                values.Add(name == "SourceTradeId" ? "42" : name == "Symbol" ? "'SPY'" : name.EndsWith("Utc") ? "'2026-11-23 12:00:00'" : reader.GetInt32(3) == 0 ? "NULL" : "'0'");
            }
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO PostTradeAnalysisRecords ({string.Join(',', fields)}) VALUES ({string.Join(',', values)})";
            await insert.ExecuteNonQueryAsync(); await insert.ExecuteNonQueryAsync();
        }
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var old = await db.PostTradeAnalysisRecords.ToArrayAsync();
        Assert.Equal(2, old.Length); Assert.All(old, x => { Assert.Equal(42, x.SourceTradeId); Assert.Null(x.ExitManagementId); Assert.Null(x.FeesComplete); });
        old[0].ExitManagementId = 123; await db.SaveChangesAsync();
        old[1].ExitManagementId = 123;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        old[1].ExitManagementId = 124; // Different lifecycle cannot create a second automatic result for the same trade.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
