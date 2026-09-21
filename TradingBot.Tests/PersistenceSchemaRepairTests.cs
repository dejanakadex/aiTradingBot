using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class PersistenceSchemaRepairTests
    {
        [Fact]
        public async Task MigrationHostedService_RepairsMissingAiMetadataColumns()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"tradingbot-schema-drift-{Guid.NewGuid():N}.db");
            try
            {
                await CreateDriftedDatabaseAsync(dbPath);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
                using (var provider = services.BuildServiceProvider())
                {
                    var service = new MigrationHostedService(
                        provider,
                        provider.GetRequiredService<ILogger<MigrationHostedService>>());

                    await service.StartAsync(CancellationToken.None);
                }

                await using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                Assert.True(await ColumnExistsAsync(connection, "AiAnalysisRecords", "AgentType"));
                Assert.True(await ColumnExistsAsync(connection, "AiAnalysisRecords", "Model"));
                Assert.True(await ColumnExistsAsync(connection, "AiAnalysisRecords", "PromptVersion"));
                Assert.True(await ColumnExistsAsync(connection, "AiApiUsageRecords", "AgentType"));
                Assert.True(await ColumnExistsAsync(connection, "AiApiUsageRecords", "PromptVersion"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        [Fact]
        public async Task MigrationHostedService_CreatesMissingAiApiUsageRecordsTable()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"tradingbot-schema-drift-{Guid.NewGuid():N}.db");
            try
            {
                await CreateDatabaseMissingAiUsageTableAsync(dbPath);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
                using (var provider = services.BuildServiceProvider())
                {
                    var service = new MigrationHostedService(
                        provider,
                        provider.GetRequiredService<ILogger<MigrationHostedService>>());

                    await service.StartAsync(CancellationToken.None);
                }

                await using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                Assert.True(await TableExistsAsync(connection, "AiApiUsageRecords"));
                Assert.True(await ColumnExistsAsync(connection, "AiApiUsageRecords", "AgentType"));
                Assert.True(await ColumnExistsAsync(connection, "AiApiUsageRecords", "RequestType"));
                Assert.True(await ColumnExistsAsync(connection, "AiApiUsageRecords", "PromptVersion"));

                var options = new DbContextOptionsBuilder<TradingBotDbContext>()
                    .UseSqlite(connection)
                    .Options;
                await using var db = new TradingBotDbContext(options);
                Assert.Equal(0, await db.AiApiUsageRecords.CountAsync());
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        [Fact]
        public async Task MigrationHostedService_RepairsMissingOrderClientOrderKeyColumn()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"tradingbot-schema-drift-{Guid.NewGuid():N}.db");
            try
            {
                await CreateDatabaseMissingOrderClientOrderKeyAsync(dbPath);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
                using (var provider = services.BuildServiceProvider())
                {
                    var service = new MigrationHostedService(
                        provider,
                        provider.GetRequiredService<ILogger<MigrationHostedService>>());

                    await service.StartAsync(CancellationToken.None);
                }

                await using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                Assert.True(await ColumnExistsAsync(connection, "OrderRecords", "ClientOrderKey"));
                Assert.True(await IndexExistsAsync(connection, "IX_OrderRecords_ClientOrderKey"));

                var options = new DbContextOptionsBuilder<TradingBotDbContext>()
                    .UseSqlite(connection)
                    .Options;
                await using var db = new TradingBotDbContext(options);
                Assert.Equal(0, await db.OrderRecords.CountAsync());
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }
            }
        }

        private static async Task CreateDriftedDatabaseAsync(string dbPath)
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            var sql = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
                    ('20260824125426_InitialCreate', '9.0.14'),
                    ('20260824170000_AddPostTradeAnalysisRecords', '9.0.14'),
                    ('20260824180000_AddAiApiUsageRecords', '9.0.14'),
                    ('20260824183000_AddOrderClientOrderKey', '9.0.14'),
                    ('20260824192000_AddAiAgentMetadata', '9.0.14');

                CREATE TABLE "AiAnalysisRecords" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AiAnalysisRecords" PRIMARY KEY AUTOINCREMENT,
                    "TimestampUtc" TEXT NOT NULL,
                    "Context" TEXT NOT NULL,
                    "ResultJson" TEXT NOT NULL
                );

                CREATE TABLE "AiApiUsageRecords" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AiApiUsageRecords" PRIMARY KEY AUTOINCREMENT,
                    "RequestType" TEXT NOT NULL,
                    "Symbol" TEXT NOT NULL,
                    "Pattern" TEXT NOT NULL,
                    "TimestampUtc" TEXT NOT NULL,
                    "InputTokens" INTEGER NULL,
                    "OutputTokens" INTEGER NULL,
                    "TotalTokens" INTEGER NULL,
                    "CachedInputTokens" INTEGER NULL,
                    "DurationMs" INTEGER NOT NULL,
                    "Model" TEXT NOT NULL,
                    "Success" INTEGER NOT NULL,
                    "FailureReason" TEXT NOT NULL
                );
                """;

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private static async Task CreateDatabaseMissingOrderClientOrderKeyAsync(string dbPath)
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            var sql = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
                    ('20260824125426_InitialCreate', '9.0.14'),
                    ('20260824170000_AddPostTradeAnalysisRecords', '9.0.14'),
                    ('20260824180000_AddAiApiUsageRecords', '9.0.14'),
                    ('20260824183000_AddOrderClientOrderKey', '9.0.14'),
                    ('20260824192000_AddAiAgentMetadata', '9.0.14');

                CREATE TABLE "OrderRecords" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_OrderRecords" PRIMARY KEY AUTOINCREMENT,
                    "BrokerOrderId" TEXT NOT NULL,
                    "Symbol" TEXT NOT NULL,
                    "Side" TEXT NOT NULL,
                    "Quantity" TEXT NOT NULL,
                    "Price" TEXT NULL,
                    "Status" TEXT NOT NULL,
                    "SubmittedUtc" TEXT NOT NULL,
                    "RawJson" TEXT NOT NULL
                );
                CREATE INDEX "IX_OrderRecords_BrokerOrderId" ON "OrderRecords" ("BrokerOrderId");

                CREATE TABLE "AiAnalysisRecords" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AiAnalysisRecords" PRIMARY KEY AUTOINCREMENT,
                    "TimestampUtc" TEXT NOT NULL,
                    "AgentType" TEXT NOT NULL DEFAULT '',
                    "Model" TEXT NOT NULL DEFAULT '',
                    "PromptVersion" TEXT NOT NULL DEFAULT '',
                    "Context" TEXT NOT NULL,
                    "ResultJson" TEXT NOT NULL
                );

                CREATE TABLE "AiApiUsageRecords" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AiApiUsageRecords" PRIMARY KEY AUTOINCREMENT,
                    "AgentType" TEXT NOT NULL DEFAULT '',
                    "RequestType" TEXT NOT NULL,
                    "Symbol" TEXT NOT NULL,
                    "Pattern" TEXT NOT NULL,
                    "TimestampUtc" TEXT NOT NULL,
                    "InputTokens" INTEGER NULL,
                    "OutputTokens" INTEGER NULL,
                    "TotalTokens" INTEGER NULL,
                    "CachedInputTokens" INTEGER NULL,
                    "DurationMs" INTEGER NOT NULL,
                    "Model" TEXT NOT NULL,
                    "PromptVersion" TEXT NOT NULL DEFAULT '',
                    "Success" INTEGER NOT NULL,
                    "FailureReason" TEXT NOT NULL
                );
                """;

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private static async Task CreateDatabaseMissingAiUsageTableAsync(string dbPath)
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            var sql = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
                    ('20260824125426_InitialCreate', '9.0.14'),
                    ('20260824170000_AddPostTradeAnalysisRecords', '9.0.14'),
                    ('20260824180000_AddAiApiUsageRecords', '9.0.14'),
                    ('20260824183000_AddOrderClientOrderKey', '9.0.14'),
                    ('20260824192000_AddAiAgentMetadata', '9.0.14');

                CREATE TABLE "AiAnalysisRecords" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AiAnalysisRecords" PRIMARY KEY AUTOINCREMENT,
                    "TimestampUtc" TEXT NOT NULL,
                    "AgentType" TEXT NOT NULL DEFAULT '',
                    "Model" TEXT NOT NULL DEFAULT '',
                    "PromptVersion" TEXT NOT NULL DEFAULT '',
                    "Context" TEXT NOT NULL,
                    "ResultJson" TEXT NOT NULL
                );
                """;

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
            command.Parameters.AddWithValue("$tableName", tableName);
            return await command.ExecuteScalarAsync() != null;
        }

        private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string indexName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $indexName LIMIT 1;";
            command.Parameters.AddWithValue("$indexName", indexName);
            return await command.ExecuteScalarAsync() != null;
        }
    }
}
