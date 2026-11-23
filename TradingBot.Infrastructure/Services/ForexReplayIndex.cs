using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services;

/// <summary>Disposable disk index, never operational state. Rebuilt from verified input after interruption.</summary>
internal sealed class ForexReplayIndex : IDisposable
{
    public const string Version = "fx-stream-replay-v1";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitradingbot-fx-{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    public int EventCount { get; private set; }
    public long DuplicateEvents { get; private set; }
    public int MaximumBufferedEvents { get; private set; }
    public string CanonicalSha256 { get; private set; } = "";

    public ForexReplayIndex()
    {
        using (new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        _connection = new(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=FILE; PRAGMA cache_size=-16384; PRAGMA mmap_size=0;
            CREATE TABLE Events(Id TEXT PRIMARY KEY, PairId TEXT NOT NULL, Side TEXT NOT NULL, Received INTEGER NOT NULL, Json TEXT NOT NULL);
            CREATE INDEX EventTime ON Events(Received,Id);
            CREATE UNIQUE INDEX EventPair ON Events(PairId,Side);
            CREATE TABLE Quotes(Id TEXT PRIMARY KEY, Available INTEGER NOT NULL, Json TEXT NOT NULL);
            CREATE INDEX QuoteTime ON Quotes(Available,Id);
            CREATE TABLE Atr(At INTEGER PRIMARY KEY, Value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public async Task LoadAsync(IMarketDatasetStore source, ReplayStartRequest request, string symbol,
        ShortAggregateSettings aggregates, CancellationToken token)
    {
        await foreach (var batch in source.ReadBatchesAsync(new(request.InstrumentId, request.FromUtc, request.ToUtc, ["bid", "ask"]),
            request.ForexBatchSize, token).WithCancellation(token))
        {
            MaximumBufferedEvents = Math.Max(MaximumBufferedEvents, batch.Count);
            using var transaction = _connection.BeginTransaction();
            using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO Events VALUES($id,$pair,$side,$received,$json)";
            foreach (var name in new[] { "$id", "$pair", "$side", "$received", "$json" }) insert.Parameters.Add(new SqliteParameter(name, null));
            using var existing = _connection.CreateCommand(); existing.Transaction = transaction;
            existing.CommandText = "SELECT Json FROM Events WHERE Id=$id"; existing.Parameters.Add(new SqliteParameter("$id", null));
            foreach (var row in batch)
            {
                token.ThrowIfCancellationRequested();
                if (row.ReceivedTimeUtc > request.ToUtc) continue;
                if (row.EventId.Length < 5 || !row.EventId.EndsWith(":" + row.DataType, StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid FX event identity.");
                var json = JsonSerializer.Serialize(row);
                insert.Parameters["$id"].Value = row.EventId; insert.Parameters["$pair"].Value = row.EventId[..^4];
                insert.Parameters["$side"].Value = row.DataType; insert.Parameters["$received"].Value = row.ReceivedTimeUtc.Ticks;
                insert.Parameters["$json"].Value = json;
                if (insert.ExecuteNonQuery() == 1) EventCount = checked(EventCount + 1);
                else
                {
                    existing.Parameters["$id"].Value = row.EventId;
                    if (!string.Equals(existing.ExecuteScalar() as string, json, StringComparison.Ordinal))
                        throw new InvalidDataException("Conflicting duplicate FX event. Overlapping source files cannot override evidence.");
                    DuplicateEvents++;
                }
            }
            transaction.Commit();
        }
        if (EventCount == 0) throw new InvalidDataException("No FX paired quote data.");
        using (var incomplete = _connection.CreateCommand())
        {
            incomplete.CommandText = "SELECT COUNT(*),MIN(Received),MAX(Received) FROM (SELECT PairId,MIN(Received) Received FROM Events GROUP BY PairId HAVING COUNT(*)<>2)";
            using var reader = incomplete.ExecuteReader(); reader.Read();
            if (reader.GetInt64(0) > 0)
                throw new InvalidDataException($"Incomplete FX quote pairs: {reader.GetInt64(0)}; first received {new DateTime(reader.GetInt64(1), DateTimeKind.Utc):O}, last received {new DateTime(reader.GetInt64(2), DateTimeKind.Utc):O}. Preserve the source and review coverage; do not infer missing sides.");
        }
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT Json FROM Events ORDER BY Received,Id";
            using var reader = command.ExecuteReader();
            while (reader.Read()) { token.ThrowIfCancellationRequested(); hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + "\n")); }
            CanonicalSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        using var tx = _connection.BeginTransaction();
        using var pairs = _connection.CreateCommand(); pairs.Transaction = tx;
        pairs.CommandText = "SELECT b.Json,a.Json FROM Events b LEFT JOIN Events a ON a.PairId=b.PairId AND a.Side='ask' WHERE b.Side='bid' ORDER BY b.Received,b.Id";
        using var write = _connection.CreateCommand(); write.Transaction = tx;
        write.CommandText = "INSERT INTO Quotes VALUES($id,$at,$json)";
        foreach (var name in new[] { "$id", "$at", "$json" }) write.Parameters.Add(new SqliteParameter(name, null));
        DateTime? previous = null; var quoteCount = 0;
        using (var reader = pairs.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.IsDBNull(1)) throw new InvalidDataException("Incomplete FX quote pair.");
                var q = ForexTradeSimulator.ReadQuotes([JsonSerializer.Deserialize<MarketDatasetRecord>(reader.GetString(0))!,
                    JsonSerializer.Deserialize<MarketDatasetRecord>(reader.GetString(1))!], request.InstrumentId, symbol, aggregates).Single();
                if (previous.HasValue && q.EventUtc < previous) throw new InvalidDataException("Out-of-order FX event times require a separate coverage range.");
                previous = q.EventUtc;
                write.Parameters["$id"].Value = q.Id; write.Parameters["$at"].Value = q.AvailableUtc.Ticks;
                write.Parameters["$json"].Value = JsonSerializer.Serialize(q); write.ExecuteNonQuery(); quoteCount++;
            }
        }
        if ((long)quoteCount * 2 != EventCount) throw new InvalidDataException("Incomplete FX quote pair or invalid side.");
        tx.Commit();
    }

    public IEnumerable<IReadOnlyList<MarketDatasetRecord>> ReadEventBatches(int batchSize, CancellationToken token)
    {
        long received = 0; string id = "";
        while (true)
        {
            var batch = new List<MarketDatasetRecord>(batchSize);
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Json FROM Events WHERE Received>$at OR (Received=$at AND Id>$id) ORDER BY Received,Id LIMIT $n";
                command.Parameters.AddWithValue("$at", received); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$n", batchSize);
                using var reader = command.ExecuteReader();
                while (reader.Read()) { token.ThrowIfCancellationRequested(); batch.Add(JsonSerializer.Deserialize<MarketDatasetRecord>(reader.GetString(0))!); }
            }
            if (batch.Count == 0) yield break;
            received = batch[^1].ReceivedTimeUtc.Ticks; id = batch[^1].EventId;
            yield return batch;
        }
    }

    // Repeatable lazy range: one decision quote plus future observations, no materialized tick window.
    public IEnumerable<ForexReplayQuote> Quotes(DateTime decision, DateTime end, CancellationToken token)
    {
        using (var prior = _connection.CreateCommand())
        {
            prior.CommandText = "SELECT Json FROM Quotes WHERE Available<=$at ORDER BY Available DESC,Id DESC LIMIT 1";
            prior.Parameters.AddWithValue("$at", decision.Ticks);
            if (prior.ExecuteScalar() is string json) yield return JsonSerializer.Deserialize<ForexReplayQuote>(json)!;
        }
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Json FROM Quotes WHERE Available>$at AND Available<=$end ORDER BY Available,Id";
        command.Parameters.AddWithValue("$at", decision.Ticks); command.Parameters.AddWithValue("$end", end.Ticks);
        using var reader = command.ExecuteReader();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); yield return JsonSerializer.Deserialize<ForexReplayQuote>(reader.GetString(0))!; }
    }

    public void AddAtr(ForexReplayAtr atr)
    {
        using var command = _connection.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO Atr VALUES($at,$value)";
        command.Parameters.AddWithValue("$at", atr.AvailableUtc.Ticks); command.Parameters.AddWithValue("$value", atr.Atr.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public ForexReplayAtr[] Atr(DateTime from, DateTime to)
    {
        var result = new List<ForexReplayAtr>();
        using var command = _connection.CreateCommand(); command.CommandText = "SELECT At,Value FROM Atr WHERE At>=$from AND At<=$to ORDER BY At";
        command.Parameters.AddWithValue("$from", from.Ticks); command.Parameters.AddWithValue("$to", to.Ticks);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(new(reader.GetInt64(0), DateTimeKind.Utc), decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
        return result.ToArray();
    }

    public void Dispose()
    {
        _connection.Dispose();
        // Only the exact randomly named file created by this instance; never a recursive directory deletion.
        File.Delete(_path);
    }
}
