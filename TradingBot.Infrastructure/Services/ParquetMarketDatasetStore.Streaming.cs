using System.Runtime.CompilerServices;
using Parquet;
using Parquet.Serialization;
using TradingBot.Application.DTOs;

namespace TradingBot.Infrastructure.Services;

public sealed partial class ParquetMarketDatasetStore
{
    public const int MaximumStreamingRowGroupRows = 65536;

    public async IAsyncEnumerable<IReadOnlyList<MarketDatasetRecord>> ReadBatchesAsync(DatasetQuery query, int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 16384) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var from = query.FromUtc.HasValue ? ToUtc(query.FromUtc.Value) : (DateTime?)null;
        var to = query.ToUtc.HasValue ? ToUtc(query.ToUtc.Value) : (DateTime?)null;
        if (from >= to) throw new ArgumentException("Dataset range must be positive.");
        var types = query.DataTypes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = await LoadManifestCoreAsync(cancellationToken).ConfigureAwait(false);
            var batch = new List<MarketDatasetRecord>(batchSize);
            foreach (var file in manifest.Files.Where(x => (query.InstrumentId == null || x.InstrumentId == query.InstrumentId)
                && (types == null || types.Count == 0 || types.Contains(x.DataType))
                && (!from.HasValue || x.MaximumEventTimeUtc >= from) && (!to.HasValue || x.MinimumEventTimeUtc < to))
                .OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveManifestPath(file.RelativePath);
                if (!string.Equals(await ComputeFileHashAsync(path, cancellationToken), file.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Dataset file hash mismatch: {file.RelativePath}");
                int groups;
                await using (var metadataStream = File.OpenRead(path))
                await using (var reader = await ParquetReader.CreateAsync(metadataStream, cancellationToken: cancellationToken))
                {
                    groups = reader.RowGroupCount;
                    long count = 0;
                    for (var i = 0; i < groups; i++)
                    {
                        using var group = reader.OpenRowGroupReader(i);
                        if (group.RowCount > MaximumStreamingRowGroupRows)
                            throw new InvalidDataException("Parquet row group exceeds bounded reader capacity; re-export with row groups of at most 65,536 rows.");
                        count += group.RowCount;
                    }
                    if (count != file.RowCount) throw new InvalidDataException("Parquet row count differs from frozen manifest.");
                }
                for (var i = 0; i < groups; i++)
                {
                    await using var stream = File.OpenRead(path);
                    var rows = await ParquetSerializer.DeserializeAsync<MarketDatasetRecord>(stream, rowGroupIndex: i, cancellationToken: cancellationToken);
                    foreach (var raw in rows.Data)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var row = NormalizeReadRecord(raw);
                        if (row.InstrumentId != file.InstrumentId || row.DataType != file.DataType || row.SchemaVersion != file.SchemaVersion
                            || row.EventTimeUtc < file.MinimumEventTimeUtc || row.EventTimeUtc > file.MaximumEventTimeUtc)
                            throw new InvalidDataException("Parquet row contradicts its frozen manifest partition or time bounds.");
                        if ((query.InstrumentId != null && row.InstrumentId != query.InstrumentId)
                            || (types is { Count: > 0 } && !types.Contains(row.DataType))
                            || (from.HasValue && row.EventTimeUtc < from) || (to.HasValue && row.EventTimeUtc >= to)) continue;
                        batch.Add(row);
                        if (batch.Count == batchSize) { yield return batch; batch = new(batchSize); }
                    }
                }
            }
            if (batch.Count > 0) yield return batch;
        }
        finally { _gate.Release(); }
    }
}
