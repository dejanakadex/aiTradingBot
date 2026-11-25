using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services;

public sealed record ForexDerivationProof(string Version, string InstrumentId, DateTime FromUtc, DateTime ToUtc,
    string ConfigSha256, string PlanSha256, string SourceManifestSha256, string SourceCanonicalSha256,
    string OutputManifestSha256, string RetainedCanonicalSha256, string LedgerSha256, string CoverageSha256,
    long SourceEvents, long RetainedEvents, long ExcludedEvents, int ExcludedPairs,
    ForexReplayCoverageEvidence OriginalCoverage, bool StrictReplayInputVerified = true,
    bool N5Complete = false, bool CanSendOrders = false);

/// <summary>Explicit offline derivation; all excluded diagnostic rows remain in a complete ledger and in the raw source.</summary>
public static class ForexDatasetDerivation
{
    public const string Version = "fx-derivation-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static ParquetMarketDatasetStore Store(string path) => new(OptionsFactory.Create(new DatasetStorageSettings { RootPath = path }),
        new SystemClock(), NullLogger<ParquetMarketDatasetStore>.Instance);
    private static bool Inside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static async Task<string> HashFile(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }

    public static async Task<ForexDerivationProof> DeriveAsync(string sourcePath, string outputPath, TradingSettings trading,
        ShortAggregateSettings aggregates, string instrumentId, DateTime from, DateTime to, ForexReplayCoverageEvidence coverage,
        string configSha256, string planSha256, int batchSize = 4096, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(outputPath)
            || !Directory.Exists(sourcePath) || Directory.Exists(outputPath) || File.Exists(outputPath)
            || from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from >= to || to - from > TimeSpan.FromDays(7)
            || batchSize is < 1 or > 16384 || aggregates.GetValidationErrors().Count != 0
            || !IsHash(configSha256) || !IsHash(planSha256))
            throw new ArgumentException("Derivation requires an existing raw source, a new absolute output, valid settings/hashes and a UTC range of at most seven days.");
        sourcePath = Path.GetFullPath(sourcePath); outputPath = Path.GetFullPath(outputPath);
        if (Inside(sourcePath, outputPath) || Inside(outputPath, sourcePath)) throw new ArgumentException("Raw source and derived output must be separate.");
        coverage = ForexReplayCoveragePolicy.FreezeEvidence(coverage, instrumentId, from, to)
            ?? throw new ArgumentException("Explicit coverage evidence is required, even when it has no recorded interruptions.");
        if (coverage.Interruptions.Length > 10000) throw new ArgumentException("At most 10000 input interruptions are supported.");
        var instrument = trading.GetConfiguredInstruments().Single(x => x.InstrumentId == instrumentId);
        if (instrument.InstrumentType != InstrumentType.Forex) throw new ArgumentException("FX instrument required.");
        var source = Store(sourcePath);
        if (await source.GetBoundForexCoverageAsync(token) != null) throw new InvalidDataException("Derive from the frozen raw source, not an existing derivation.");
        var before = await source.VerifyAsync(token);
        if (!before.IsValid) throw new InvalidDataException("Raw source integrity failed.");
        using var index = new ForexReplayIndex();
        var request = new ReplayStartRequest(instrumentId, from, to) { ForexBatchSize = batchSize };
        await index.LoadAuditAsync(source, request, token);
        // Reject even identical duplicates: this ledger accounts for every selected source occurrence.
        if (index.DuplicateEvents != 0) throw new InvalidDataException("Resolve duplicate occurrences explicitly before derivation; no silent deduplication.");
        var stage = outputPath + ".incomplete-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage); // On failure retain this clearly incomplete directory for diagnosis.
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var derivedGaps = new List<ForexReplayInterruption>();
        DateTime? previousHealthy = null, pendingStart = null;
        int excludedPairs = 0;
        DateTime Clamp(DateTime value) => value < from ? from : value > to ? to : value;
        void EndGap(DateTime end)
        {
            if (!pendingStart.HasValue) return;
            derivedGaps.Add(new(pendingStart.Value, Clamp(end), "Derived uncertainty around excluded diagnostic quote pair(s); not proof of a broker outage. See exclusions.jsonl."));
            pendingStart = null;
        }
        await using (var ledger = new StreamWriter(new FileStream(Path.Combine(stage, "exclusions.jsonl"), FileMode.CreateNew), new UTF8Encoding(false)))
        {
            ledger.NewLine = "\n";
            foreach (var (rows, rejected) in index.ReadAuditPairs(instrumentId, instrument.Symbol, aggregates, token))
            {
                if (!rejected) { EndGap(rows[0].ReceivedTimeUtc); previousHealthy = rows[0].ReceivedTimeUtc; continue; }
                if (++excludedPairs > 10000) throw new InvalidDataException("More than 10000 rejected pairs; use a smaller explicit range.");
                var start = Clamp(new[] { previousHealthy ?? from, rows[0].EventTimeUtc, rows[0].ReceivedTimeUtc }.Min());
                pendingStart = pendingStart.HasValue && pendingStart < start ? pendingStart : start;
                foreach (var row in rows.OrderBy(x => x.EventId, StringComparer.Ordinal))
                {
                    excluded.Add(row.EventId);
                    await ledger.WriteLineAsync(JsonSerializer.Serialize(new { Disposition = "ExcludedDiagnosticPair", PairId = row.EventId[..^4], Record = row }).AsMemory(), token);
                }
            }
        }
        EndGap(to);
        var effective = new ForexReplayCoverageEvidence(instrumentId, from, to,
            $"{Version}; raw manifest {before.ManifestSha256}; original coverage retained in derivation.json; exclusions.jsonl retained in full.",
            coverage.Interruptions.Where(x => x.EndUtc >= from && x.StartUtc <= to)
                .Select(x => new ForexReplayInterruption(Clamp(x.StartUtc), Clamp(x.EndUtc), x.Reason)).Concat(derivedGaps).ToArray());
        effective = ForexReplayCoveragePolicy.FreezeEvidence(effective, instrumentId, from, to)!;
        var output = Store(stage); long retained = 0;
        var writeBuffer = new List<MarketDatasetRecord>(4096);
        using var expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var batch in index.ReadEventBatches(batchSize, token))
        {
            var kept = batch.Where(x => !excluded.Contains(x.EventId)).ToArray();
            foreach (var row in kept)
            {
                expectedHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n"));
                writeBuffer.Add(row);
                if (writeBuffer.Count == 4096) { await output.WriteBatchAsync(writeBuffer, token); writeBuffer.Clear(); }
            }
            retained += kept.Length;
        }
        if (writeBuffer.Count > 0) await output.WriteBatchAsync(writeBuffer, token);
        var keptHash = Convert.ToHexString(expectedHash.GetHashAndReset());
        using (var strict = new ForexReplayIndex())
        {
            await strict.LoadAsync(output, request, instrument.Symbol, aggregates, token);
            if (strict.EventCount != retained || !strict.CanonicalSha256.Equals(keptHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Derived content differs from the explicitly retained source records.");
        }
        await File.WriteAllTextAsync(Path.Combine(stage, "coverage.json"), JsonSerializer.Serialize(effective, Json), token);
        var coverageHash = await HashFile(Path.Combine(stage, "coverage.json"), token);
        var proof = new ForexDerivationProof(Version, instrumentId, from, to, configSha256, planSha256, before.ManifestSha256,
            index.CanonicalSha256, "", keptHash, await HashFile(Path.Combine(stage, "exclusions.jsonl"), token),
            coverageHash, index.EventCount, retained, excluded.Count, excludedPairs, coverage);
        var manifest = await output.GetManifestAsync(token);
        manifest.ForexDerivationVersion = Version; manifest.RequiredForexCoverageSha256 = coverageHash;
        manifest.ForexDerivationProofSha256 = HashProof(proof);
        var manifestRaw = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        await File.WriteAllBytesAsync(Path.Combine(stage, "_manifest.json"), manifestRaw, token);
        await File.WriteAllTextAsync(Path.Combine(stage, "_manifest.sha256"), Convert.ToHexString(SHA256.HashData(manifestRaw)), token);
        var verified = await output.VerifyAsync(token); var after = await source.VerifyAsync(token);
        if (!verified.IsValid || !after.IsValid || before.ManifestSha256 != after.ManifestSha256)
            throw new InvalidDataException("Source changed or derived integrity failed; no completed output published.");
        proof = proof with { OutputManifestSha256 = verified.ManifestSha256 };
        await File.WriteAllTextAsync(Path.Combine(stage, "derivation.json"), JsonSerializer.Serialize(proof, Json), token);
        _ = await ReadBoundCoverageAsync(stage, token);
        token.ThrowIfCancellationRequested();
        Directory.Move(stage, outputPath); // Same parent, new destination only; source and existing outputs never moved or overwritten.
        return proof;
    }

    public static async Task<ForexReplayCoverageEvidence?> ReadBoundCoverageAsync(string root, CancellationToken token = default)
    {
        var manifestPath = Path.Combine(root, "_manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var raw = await File.ReadAllBytesAsync(manifestPath, token);
        var manifest = JsonSerializer.Deserialize<DatasetManifest>(raw, Json) ?? throw new InvalidDataException("Manifest is null.");
        var proofPath = Path.Combine(root, "derivation.json");
        if (manifest.ForexDerivationVersion == null && manifest.RequiredForexCoverageSha256 == null && manifest.ForexDerivationProofSha256 == null && !File.Exists(proofPath)) return null;
        if (manifest.ForexDerivationVersion != Version || !IsHash(manifest.RequiredForexCoverageSha256)
            || !IsHash(manifest.ForexDerivationProofSha256) || !File.Exists(proofPath))
            throw new InvalidDataException("Derived FX dataset is missing its required provenance/coverage binding.");
        var proof = JsonSerializer.Deserialize<ForexDerivationProof>(await File.ReadAllTextAsync(proofPath, token), Json)
            ?? throw new InvalidDataException("Derivation proof is null.");
        var actualManifest = Convert.ToHexString(SHA256.HashData(raw));
        if (proof.Version != Version || proof.CanSendOrders || proof.N5Complete || !proof.StrictReplayInputVerified
            || !IsHash(proof.OutputManifestSha256) || !IsHash(proof.CoverageSha256) || !IsHash(proof.LedgerSha256)
            || !IsHash(proof.ConfigSha256) || !IsHash(proof.PlanSha256) || !IsHash(proof.SourceManifestSha256)
            || !IsHash(proof.SourceCanonicalSha256) || !IsHash(proof.RetainedCanonicalSha256)
            || proof.SourceEvents != proof.RetainedEvents + proof.ExcludedEvents || proof.RetainedEvents <= 0
            || proof.ExcludedEvents < 0 || proof.ExcludedPairs < 0
            || !HashProof(proof).Equals(manifest.ForexDerivationProofSha256, StringComparison.OrdinalIgnoreCase)
            || actualManifest != proof.OutputManifestSha256.ToUpperInvariant()
            || !actualManifest.Equals((await File.ReadAllTextAsync(Path.Combine(root, "_manifest.sha256"), token)).Trim(), StringComparison.OrdinalIgnoreCase)
            || !proof.CoverageSha256.Equals(manifest.RequiredForexCoverageSha256, StringComparison.OrdinalIgnoreCase)
            || !proof.CoverageSha256.Equals(await HashFile(Path.Combine(root, "coverage.json"), token), StringComparison.OrdinalIgnoreCase)
            || !proof.LedgerSha256.Equals(await HashFile(Path.Combine(root, "exclusions.jsonl"), token), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Derived FX provenance, coverage or exclusion ledger was changed.");
        var coverage = JsonSerializer.Deserialize<ForexReplayCoverageEvidence>(await File.ReadAllTextAsync(Path.Combine(root, "coverage.json"), token), Json);
        return ForexReplayCoveragePolicy.FreezeEvidence(coverage, proof.InstrumentId, proof.FromUtc, proof.ToUtc)
            ?? throw new InvalidDataException("Derived FX coverage is null.");
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    // The manifest binds all proof metadata; omit only its own resulting digest to avoid a circular hash.
    private static string HashProof(ForexDerivationProof proof) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(proof with { OutputManifestSha256 = "" }, Json)));
}
