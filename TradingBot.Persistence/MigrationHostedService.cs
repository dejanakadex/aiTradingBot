using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TradingBot.Persistence
{
    public class MigrationHostedService : IHostedService
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<MigrationHostedService> _logger;

        public MigrationHostedService(IServiceProvider sp, ILogger<MigrationHostedService> logger)
        {
            _sp = sp;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                _logger.LogInformation("Applying database migrations if any...");
                await db.Database.MigrateAsync(cancellationToken);
                await RepairKnownSchemaDriftAsync(db, cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
                await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);
                _logger.LogInformation("Database migrations applied");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while applying migrations");
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task RepairKnownSchemaDriftAsync(TradingBotDbContext db, CancellationToken cancellationToken)
        {
            if (!await TableExistsAsync(db, "AiAnalysisRecords", cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("SQLite schema drift detected: creating missing table AiAnalysisRecords.");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "AiAnalysisRecords" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_AiAnalysisRecords" PRIMARY KEY AUTOINCREMENT,
                        "TimestampUtc" TEXT NOT NULL,
                        "AgentType" TEXT NOT NULL DEFAULT '',
                        "Model" TEXT NOT NULL DEFAULT '',
                        "PromptVersion" TEXT NOT NULL DEFAULT '',
                        "Context" TEXT NOT NULL,
                        "ResultJson" TEXT NOT NULL
                    );
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(db, "AiAnalysisRecords", cancellationToken).ConfigureAwait(false))
            {
                await AddTextColumnIfMissingAsync(db, "AiAnalysisRecords", "AgentType", cancellationToken).ConfigureAwait(false);
                await AddTextColumnIfMissingAsync(db, "AiAnalysisRecords", "Model", cancellationToken).ConfigureAwait(false);
                await AddTextColumnIfMissingAsync(db, "AiAnalysisRecords", "PromptVersion", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiAnalysisRecords_TimestampUtc ON AiAnalysisRecords (TimestampUtc);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiAnalysisRecords_AgentType ON AiAnalysisRecords (AgentType);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiAnalysisRecords_Model ON AiAnalysisRecords (Model);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiAnalysisRecords_PromptVersion ON AiAnalysisRecords (PromptVersion);", cancellationToken).ConfigureAwait(false);
            }

            if (!await TableExistsAsync(db, "AiApiUsageRecords", cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("SQLite schema drift detected: creating missing table AiApiUsageRecords.");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "AiApiUsageRecords" (
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
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(db, "AiApiUsageRecords", cancellationToken).ConfigureAwait(false))
            {
                await AddTextColumnIfMissingAsync(db, "AiApiUsageRecords", "AgentType", cancellationToken).ConfigureAwait(false);
                await AddTextColumnIfMissingAsync(db, "AiApiUsageRecords", "PromptVersion", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_AgentType ON AiApiUsageRecords (AgentType);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_Model ON AiApiUsageRecords (Model);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_Pattern ON AiApiUsageRecords (Pattern);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_PromptVersion ON AiApiUsageRecords (PromptVersion);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_RequestType ON AiApiUsageRecords (RequestType);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_Success ON AiApiUsageRecords (Success);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_Symbol ON AiApiUsageRecords (Symbol);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_AiApiUsageRecords_TimestampUtc ON AiApiUsageRecords (TimestampUtc);", cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(db, "OrderRecords", cancellationToken).ConfigureAwait(false))
            {
                await AddTextColumnIfMissingAsync(db, "OrderRecords", "ClientOrderKey", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IF NOT EXISTS IX_OrderRecords_ClientOrderKey ON OrderRecords (ClientOrderKey) WHERE ClientOrderKey <> '';",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!await TableExistsAsync(db, "ExitManagementRecords", cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("SQLite schema drift detected: creating missing table ExitManagementRecords.");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ExitManagementRecords" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ExitManagementRecords" PRIMARY KEY AUTOINCREMENT,
                        "TradeId" INTEGER NULL,
                        "Symbol" TEXT NOT NULL,
                        "EntryBrokerOrderId" TEXT NOT NULL,
                        "ProtectiveStopBrokerOrderId" TEXT NOT NULL,
                        "State" INTEGER NOT NULL,
                        "InitialEntryPrice" TEXT NOT NULL,
                        "InitialStopPrice" TEXT NOT NULL,
                        "InitialRiskPerShare" TEXT NOT NULL,
                        "FilledQuantity" TEXT NOT NULL,
                        "ProtectedQuantity" TEXT NOT NULL,
                        "HighestPriceSinceEntry" TEXT NOT NULL,
                        "CurrentProtectiveStop" TEXT NOT NULL,
                        "BreakEvenActivated" INTEGER NOT NULL,
                        "TrailingActivated" INTEGER NOT NULL,
                        "TrailingAtrTimeframe" TEXT NOT NULL,
                        "TrailingAtrMultiplier" TEXT NOT NULL,
                        "OpenedUtc" TEXT NOT NULL,
                        "UpdatedUtc" TEXT NOT NULL,
                        "ClosedUtc" TEXT NULL,
                        "RawJson" TEXT NOT NULL
                    );
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(db, "ExitManagementRecords", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_ExitManagementRecords_EntryBrokerOrderId ON ExitManagementRecords (EntryBrokerOrderId);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitManagementRecords_ProtectiveStopBrokerOrderId ON ExitManagementRecords (ProtectiveStopBrokerOrderId);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitManagementRecords_State ON ExitManagementRecords (State);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitManagementRecords_Symbol ON ExitManagementRecords (Symbol);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitManagementRecords_UpdatedUtc ON ExitManagementRecords (UpdatedUtc);", cancellationToken).ConfigureAwait(false);
            }

            if (!await TableExistsAsync(db, "ExitStopAuditRecords", cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("SQLite schema drift detected: creating missing table ExitStopAuditRecords.");
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "ExitStopAuditRecords" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ExitStopAuditRecords" PRIMARY KEY AUTOINCREMENT,
                        "TradeId" INTEGER NULL,
                        "Symbol" TEXT NOT NULL,
                        "EntryBrokerOrderId" TEXT NOT NULL,
                        "BrokerOrderId" TEXT NOT NULL,
                        "OldStop" TEXT NULL,
                        "NewStop" TEXT NOT NULL,
                        "Reason" TEXT NOT NULL,
                        "MarketPrice" TEXT NOT NULL,
                        "HighestPriceSinceEntry" TEXT NOT NULL,
                        "CurrentAtr" TEXT NULL,
                        "CurrentRMultiple" TEXT NOT NULL,
                        "ExitManagementState" TEXT NOT NULL,
                        "TimestampUtc" TEXT NOT NULL
                    );
                    """,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await TableExistsAsync(db, "ExitStopAuditRecords", cancellationToken).ConfigureAwait(false))
            {
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitStopAuditRecords_BrokerOrderId ON ExitStopAuditRecords (BrokerOrderId);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitStopAuditRecords_EntryBrokerOrderId ON ExitStopAuditRecords (EntryBrokerOrderId);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitStopAuditRecords_Symbol ON ExitStopAuditRecords (Symbol);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitStopAuditRecords_TimestampUtc ON ExitStopAuditRecords (TimestampUtc);", cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_ExitStopAuditRecords_TradeId ON ExitStopAuditRecords (TradeId);", cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task AddTextColumnIfMissingAsync(TradingBotDbContext db, string tableName, string columnName, CancellationToken cancellationToken)
        {
            if (await ColumnExistsAsync(db, tableName, columnName, cancellationToken).ConfigureAwait(false)) return;

            _logger.LogWarning("SQLite schema drift detected: adding missing column {Table}.{Column}", tableName, columnName);
#pragma warning disable EF1002 // Identifiers are internal fixed table/column names and are quoted before use.
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE {QuoteIdentifier(tableName)} ADD COLUMN {QuoteIdentifier(columnName)} TEXT NOT NULL DEFAULT '';",
                cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        }

        private static async Task<bool> TableExistsAsync(TradingBotDbContext db, string tableName, CancellationToken cancellationToken)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
            AddParameter(command, "$tableName", tableName);
            await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result != null;
        }

        private static async Task<bool> ColumnExistsAsync(TradingBotDbContext db, string tableName, string columnName, CancellationToken cancellationToken)
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            await EnsureOpenAsync(db, cancellationToken).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task EnsureOpenAsync(TradingBotDbContext db, CancellationToken cancellationToken)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static string QuoteIdentifier(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
    }
}
