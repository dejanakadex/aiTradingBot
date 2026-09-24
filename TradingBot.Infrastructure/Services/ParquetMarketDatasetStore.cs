using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parquet.Serialization;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public sealed class ParquetMarketDatasetStore : IMarketDatasetStore
    {
        private const string ManifestFileName = "_manifest.json";
        private const string ManifestHashFileName = "_manifest.sha256";
        private static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private readonly DatasetStorageSettings _settings;
        private readonly IClock _clock;
        private readonly ILogger<ParquetMarketDatasetStore> _logger;
        private readonly string _rootPath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ParquetMarketDatasetStore(
            IOptions<DatasetStorageSettings> settings,
            IClock clock,
            ILogger<ParquetMarketDatasetStore> logger)
        {
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rootPath = Path.GetFullPath(_settings.RootPath);
        }

        public async Task<DatasetWriteResult> WriteBatchAsync(
            IReadOnlyCollection<MarketDatasetRecord> records,
            CancellationToken cancellationToken = default)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (!_settings.Enabled || records.Count == 0) return new DatasetWriteResult(0, 0, 0);

            var normalized = records.Select(NormalizeAndValidate).ToArray();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(_rootPath);
                var manifest = await LoadManifestCoreAsync(cancellationToken).ConfigureAwait(false);
                if (manifest.Files.Count > 0
                    && !manifest.SchemaVersion.Equals(_settings.SchemaVersion, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Dataset schema '{manifest.SchemaVersion}' does not match configured schema '{_settings.SchemaVersion}'. Use a new root or run an explicit migration.");
                }
                var filesCreated = 0;
                var rowsWritten = 0;
                var filesSkipped = 0;
                var manifestChanged = false;

                foreach (var group in normalized.GroupBy(item => new
                         {
                             item.InstrumentId,
                             Date = item.EventTimeUtc.Date,
                             item.DataType
                         }))
                {
                    var ordered = group
                        .GroupBy(item => item.EventId, StringComparer.Ordinal)
                        .Select(item => item.First())
                        .OrderBy(item => item.EventTimeUtc)
                        .ThenBy(item => item.ReceivedTimeUtc)
                        .ThenBy(item => item.EventId, StringComparer.Ordinal)
                        .ToArray();
                    var canonicalHash = ComputeCanonicalHash(ordered);
                    var partition = Path.Combine(
                        $"instrument={SafeSegment(group.Key.InstrumentId)}",
                        $"date={group.Key.Date:yyyy-MM-dd}",
                        $"type={SafeSegment(group.Key.DataType)}");
                    var fileName = $"part-{ordered[0].EventTimeUtc:yyyyMMddTHHmmssfffffffZ}-{ordered[^1].EventTimeUtc:yyyyMMddTHHmmssfffffffZ}-{canonicalHash[..16].ToLowerInvariant()}.parquet";
                    var relativePath = Path.Combine(partition, fileName).Replace(Path.DirectorySeparatorChar, '/');
                    var finalPath = Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(finalPath))
                    {
                        filesSkipped++;
                        continue;
                    }

                    var directory = Path.GetDirectoryName(finalPath)!;
                    Directory.CreateDirectory(directory);
                    var temporaryPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        await using (var stream = new FileStream(
                            temporaryPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            131072,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            await ParquetSerializer.SerializeAsync(
                                ordered,
                                stream,
                                customMetadata: new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["schemaVersion"] = _settings.SchemaVersion,
                                    ["instrumentId"] = group.Key.InstrumentId,
                                    ["dateUtc"] = group.Key.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                    ["dataType"] = group.Key.DataType,
                                    ["canonicalSha256"] = canonicalHash
                                }).ConfigureAwait(false);
                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        File.Move(temporaryPath, finalPath, false);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }

                    var fileHash = await ComputeFileHashAsync(finalPath, cancellationToken).ConfigureAwait(false);
                    manifest.Files.Add(new DatasetFileManifestEntry
                    {
                        RelativePath = relativePath,
                        InstrumentId = group.Key.InstrumentId,
                        DateUtc = DateTime.SpecifyKind(group.Key.Date, DateTimeKind.Utc),
                        DataType = group.Key.DataType,
                        RowCount = ordered.Length,
                        MinimumEventTimeUtc = ordered[0].EventTimeUtc,
                        MaximumEventTimeUtc = ordered[^1].EventTimeUtc,
                        ContentSha256 = fileHash,
                        SchemaVersion = _settings.SchemaVersion,
                        CreatedAtUtc = ToUtc(_clock.UtcNow)
                    });
                    filesCreated++;
                    rowsWritten += ordered.Length;
                    manifestChanged = true;
                }

                if (manifestChanged)
                {
                    manifest.SchemaVersion = _settings.SchemaVersion;
                    manifest.UpdatedAtUtc = ToUtc(_clock.UtcNow);
                    manifest.Files = manifest.Files
                        .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                        .ToList();
                    await WriteManifestCoreAsync(manifest, cancellationToken).ConfigureAwait(false);
                }

                return new DatasetWriteResult(filesCreated, rowsWritten, filesSkipped);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await LoadManifestCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(
            DatasetQuery query,
            CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var fromUtc = query.FromUtc.HasValue ? ToUtc(query.FromUtc.Value) : (DateTime?)null;
            var toUtc = query.ToUtc.HasValue ? ToUtc(query.ToUtc.Value) : (DateTime?)null;
            if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value >= toUtc.Value)
            {
                throw new ArgumentException("Dataset query FromUtc must be earlier than ToUtc.", nameof(query));
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var manifest = await LoadManifestCoreAsync(cancellationToken).ConfigureAwait(false);
                var requestedTypes = query.DataTypes?
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selected = manifest.Files.Where(file =>
                    (string.IsNullOrWhiteSpace(query.InstrumentId)
                        || file.InstrumentId.Equals(query.InstrumentId.Trim(), StringComparison.OrdinalIgnoreCase))
                    && (requestedTypes == null || requestedTypes.Count == 0 || requestedTypes.Contains(file.DataType))
                    && (!fromUtc.HasValue || file.MaximumEventTimeUtc >= fromUtc.Value)
                    && (!toUtc.HasValue || file.MinimumEventTimeUtc < toUtc.Value));
                var records = new List<MarketDatasetRecord>();

                foreach (var file in selected.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
                {
                    var path = ResolveManifestPath(file.RelativePath);
                    var actualHash = await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false);
                    if (!actualHash.Equals(file.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Dataset file hash mismatch: {file.RelativePath}");
                    }

                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
                    var result = await ParquetSerializer.DeserializeAsync<MarketDatasetRecord>(stream).ConfigureAwait(false);
                    records.AddRange(result.Data.Select(NormalizeReadRecord));
                }

                return records
                    .Where(item => (string.IsNullOrWhiteSpace(query.InstrumentId)
                            || item.InstrumentId.Equals(query.InstrumentId.Trim(), StringComparison.OrdinalIgnoreCase))
                        && (requestedTypes == null || requestedTypes.Count == 0 || requestedTypes.Contains(item.DataType))
                        && (!fromUtc.HasValue || item.EventTimeUtc >= fromUtc.Value)
                        && (!toUtc.HasValue || item.EventTimeUtc < toUtc.Value))
                    .GroupBy(item => item.EventId, StringComparer.Ordinal)
                    .Select(group => group.OrderBy(item => item.ReceivedTimeUtc).First())
                    .OrderBy(item => item.EventTimeUtc)
                    .ThenBy(item => item.ReceivedTimeUtc)
                    .ThenBy(item => item.EventId, StringComparer.Ordinal)
                    .ToArray();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var errors = new List<string>();
                var manifestPath = Path.Combine(_rootPath, ManifestFileName);
                var hashPath = Path.Combine(_rootPath, ManifestHashFileName);
                if (!File.Exists(manifestPath))
                {
                    return new DatasetVerificationResult(true, 0, 0, string.Empty, Array.Empty<string>());
                }

                var manifestHash = await ComputeFileHashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(hashPath))
                {
                    errors.Add("Manifest hash file is missing.");
                }
                else
                {
                    var expectedManifestHash = (await File.ReadAllTextAsync(hashPath, cancellationToken).ConfigureAwait(false)).Trim();
                    if (!manifestHash.Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("Manifest SHA-256 mismatch.");
                    }
                }

                var manifest = await LoadManifestCoreAsync(cancellationToken).ConfigureAwait(false);
                foreach (var entry in manifest.Files)
                {
                    try
                    {
                        var path = ResolveManifestPath(entry.RelativePath);
                        if (!File.Exists(path))
                        {
                            errors.Add($"Dataset file is missing: {entry.RelativePath}");
                            continue;
                        }

                        var actual = await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false);
                        if (!actual.Equals(entry.ContentSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"Dataset file SHA-256 mismatch: {entry.RelativePath}");
                        }
                        if (!entry.SchemaVersion.Equals(manifest.SchemaVersion, StringComparison.Ordinal))
                        {
                            errors.Add($"Dataset schema mismatch: {entry.RelativePath}");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        errors.Add($"Dataset file cannot be verified: {entry.RelativePath} ({ex.Message})");
                    }
                }

                return new DatasetVerificationResult(
                    errors.Count == 0,
                    manifest.Files.Count,
                    manifest.Files.Sum(item => (long)item.RowCount),
                    manifestHash,
                    errors);
            }
            finally
            {
                _gate.Release();
            }
        }

        private MarketDatasetRecord NormalizeAndValidate(MarketDatasetRecord record)
        {
            if (record == null) throw new ArgumentException("Dataset batch contains a null record.", nameof(record));
            if (string.IsNullOrWhiteSpace(record.EventId)) throw new ArgumentException("Dataset EventId is required.", nameof(record));
            if (string.IsNullOrWhiteSpace(record.InstrumentId)) throw new ArgumentException("Dataset InstrumentId is required.", nameof(record));
            if (string.IsNullOrWhiteSpace(record.DataType)) throw new ArgumentException("Dataset DataType is required.", nameof(record));
            if (record.EventTimeUtc == default) throw new ArgumentException("Dataset EventTimeUtc is required.", nameof(record));
            if (record.ReceivedTimeUtc == default) throw new ArgumentException("Dataset ReceivedTimeUtc is required.", nameof(record));

            record.SchemaVersion = _settings.SchemaVersion;
            record.EventId = record.EventId.Trim();
            record.InstrumentId = record.InstrumentId.Trim();
            record.Symbol = record.Symbol.Trim().ToUpperInvariant();
            record.DataType = record.DataType.Trim().ToLowerInvariant();
            record.EventTimeUtc = ToUtc(record.EventTimeUtc);
            record.ReceivedTimeUtc = ToUtc(record.ReceivedTimeUtc);
            record.Source = record.Source.Trim();
            record.Timeframe = record.Timeframe.Trim().ToLowerInvariant();
            return record;
        }

        private static MarketDatasetRecord NormalizeReadRecord(MarketDatasetRecord record)
        {
            record.EventTimeUtc = ToUtc(record.EventTimeUtc);
            record.ReceivedTimeUtc = ToUtc(record.ReceivedTimeUtc);
            return record;
        }

        private async Task<DatasetManifest> LoadManifestCoreAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(_rootPath, ManifestFileName);
            if (!File.Exists(path))
            {
                return new DatasetManifest { SchemaVersion = _settings.SchemaVersion };
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<DatasetManifest>(stream, ManifestJsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Dataset manifest is empty or invalid.");
        }

        private async Task WriteManifestCoreAsync(DatasetManifest manifest, CancellationToken cancellationToken)
        {
            var manifestPath = Path.Combine(_rootPath, ManifestFileName);
            var manifestTemporaryPath = Path.Combine(_rootPath, $".{ManifestFileName}.{Guid.NewGuid():N}.tmp");
            var hashPath = Path.Combine(_rootPath, ManifestHashFileName);
            var hashTemporaryPath = Path.Combine(_rootPath, $".{ManifestHashFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(manifestTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                var hash = await ComputeFileHashAsync(manifestTemporaryPath, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(hashTemporaryPath, hash + Environment.NewLine, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
                File.Move(manifestTemporaryPath, manifestPath, true);
                File.Move(hashTemporaryPath, hashPath, true);
                _logger.LogInformation("Updated dataset manifest with {FileCount} files and SHA-256 {ManifestHash}.", manifest.Files.Count, hash);
            }
            finally
            {
                if (File.Exists(manifestTemporaryPath)) File.Delete(manifestTemporaryPath);
                if (File.Exists(hashTemporaryPath)) File.Delete(hashTemporaryPath);
            }
        }

        private string ResolveManifestPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                throw new InvalidDataException("Dataset manifest contains an invalid relative path.");
            }
            var candidate = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = _rootPath.EndsWith(Path.DirectorySeparatorChar) ? _rootPath : _rootPath + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Dataset manifest path escapes the configured root.");
            }
            return candidate;
        }

        private static string SafeSegment(string value)
        {
            var normalized = new string(value.Trim().Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray());
            if (string.IsNullOrWhiteSpace(normalized) || normalized is "." or "..")
            {
                throw new ArgumentException("Dataset partition value cannot be converted to a safe path segment.", nameof(value));
            }
            return normalized;
        }

        private static string ComputeCanonicalHash(IEnumerable<MarketDatasetRecord> records)
        {
            var builder = new StringBuilder();
            foreach (var record in records)
            {
                builder.Append(JsonSerializer.Serialize(record)).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
