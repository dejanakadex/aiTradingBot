using System.Security.Cryptography;
using System.Text.Json;
using TradingBot.Application.DTOs;

internal static class ForexDatasetSnapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Copy immutable manifest-referenced quote files, never lock or mutate the running collector.</summary>
    public static async Task<object> ExportAsync(string source, string output, string instrumentId, DateTime from, DateTime to)
    {
        if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source) || !Path.IsPathFullyQualified(output)
            || Directory.Exists(output) || File.Exists(output) || string.IsNullOrWhiteSpace(instrumentId)
            || from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from >= to)
            throw new ArgumentException("Snapshot needs an existing absolute source, a new absolute directory and a positive UTC range.");
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        if (IsInside(source, output) || IsInside(output, source)) throw new ArgumentException("Snapshot and source directories must be separate.");
        var raw = await File.ReadAllBytesAsync(Path.Combine(source, "_manifest.json"));
        var sourceHash = Hash(raw);
        if (!sourceHash.Equals((await File.ReadAllTextAsync(Path.Combine(source, "_manifest.sha256"))).Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source manifest changed during capture or is corrupt. Retry with a new destination.");
        var manifest = JsonSerializer.Deserialize<DatasetManifest>(raw, Json) ?? throw new InvalidDataException("Missing manifest.");
        if (manifest.ForexDerivationVersion != null || manifest.RequiredForexCoverageSha256 != null || manifest.ForexDerivationProofSha256 != null)
            throw new InvalidDataException("Snapshot the raw source; derived FX datasets must retain their bound coverage and derivation evidence.");
        var files = manifest.Files.Where(x => x.InstrumentId == instrumentId && x.DataType is "bid" or "ask"
            && x.MaximumEventTimeUtc >= from && x.MinimumEventTimeUtc < to).ToList();
        if (files.Count == 0) throw new InvalidDataException("Snapshot needs a nonempty quote range.");
        if (files.Select(x => x.RelativePath).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new InvalidDataException("Snapshot manifest contains repeated file entries.");
        foreach (var entry in files)
        {
            _ = Resolve(source, entry.RelativePath); _ = Resolve(output, entry.RelativePath);
            if (entry.SchemaVersion != manifest.SchemaVersion || entry.RowCount <= 0)
                throw new InvalidDataException("Invalid snapshot manifest entry.");
        }
        Directory.CreateDirectory(output);
        // On failure retain copied files for inspection. No completed manifest is written until all hashes pass.
        foreach (var entry in files)
        {
            var target = Resolve(output, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = File.OpenRead(Resolve(source, entry.RelativePath)))
            await using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                await input.CopyToAsync(copy);
            await using var check = File.OpenRead(target);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(check)).Equals(entry.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Copied quote file hash differs from captured manifest. Snapshot is incomplete.");
        }
        await File.WriteAllBytesAsync(Path.Combine(output, "_source-manifest.json"), raw);
        manifest.Files = files;
        var subset = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var subsetHash = Hash(subset);
        await File.WriteAllBytesAsync(Path.Combine(output, "_manifest.json"), subset);
        await File.WriteAllTextAsync(Path.Combine(output, "_manifest.sha256"), subsetHash);
        var report = new
        {
            Scope = "Derived immutable quote-file subset; original entry hashes preserved. Audit/replay filter range edges. No broker or database access.",
            InstrumentId = instrumentId, FromUtc = from, ToUtc = to, Files = files.Count, ManifestRows = files.Sum(x => (long)x.RowCount),
            SourceManifestSha256 = sourceHash, SnapshotManifestSha256 = subsetHash, CanSendOrders = false
        };
        await File.WriteAllTextAsync(Path.Combine(output, "snapshot-provenance.json"), JsonSerializer.Serialize(report, Json));
        return report;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static string Resolve(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !IsInside(root, full) || full == root)
            throw new InvalidDataException("Manifest path escapes dataset directory.");
        return full;
    }
}
