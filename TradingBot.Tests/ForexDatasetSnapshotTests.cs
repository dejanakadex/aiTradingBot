using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexDatasetSnapshotTests
{
    [Fact]
    public async Task SnapshotIsStableAfterSourceAppendsAndCannotOverwriteOrNest()
    {
        using var f = new ForexResearchFixture();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records());
        var output = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "snapshot");
        var start = ForexResearchFixture.Start;
        await ForexDatasetSnapshot.ExportAsync(f.DatasetPath, output, "EURUSD", start, start.AddMinutes(4));
        var snapshot = new ParquetMarketDatasetStore(Options.Create(new DatasetStorageSettings { RootPath = output }), f,
            NullLogger<ParquetMarketDatasetStore>.Instance);
        var before = await snapshot.VerifyAsync();
        Assert.True(before.IsValid); Assert.Equal(304, before.RowCount);
        var later = ForexResearchFixture.Records();
        foreach (var item in later)
        { item.EventId = item.EventId.Replace("fx10", "later"); item.EventTimeUtc = item.EventTimeUtc.AddDays(1); item.ReceivedTimeUtc = item.ReceivedTimeUtc.AddDays(1); }
        await f.Dataset.WriteBatchAsync(later);
        Assert.Equal(before.ManifestSha256, (await snapshot.VerifyAsync()).ManifestSha256);
        Assert.Equal(304, (await snapshot.ReadAsync(new("EURUSD", start, start.AddMinutes(4), ["bid", "ask"]))).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => ForexDatasetSnapshot.ExportAsync(f.DatasetPath, output, "EURUSD", start, start.AddMinutes(4)));
        await Assert.ThrowsAsync<ArgumentException>(() => ForexDatasetSnapshot.ExportAsync(f.DatasetPath, Path.Combine(f.DatasetPath, "nested"), "EURUSD", start, start.AddMinutes(4)));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("file")]
    [InlineData("path")]
    public async Task CorruptOrEscapingInputNeverProducesCompletedSnapshot(string fault)
    {
        using var f = new ForexResearchFixture();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records());
        var manifestPath = Path.Combine(f.DatasetPath, "_manifest.json");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<DatasetManifest>(await File.ReadAllTextAsync(manifestPath), json)!;
        if (fault == "manifest") await File.AppendAllTextAsync(manifestPath, " ");
        if (fault == "file") await File.AppendAllTextAsync(Path.Combine(f.DatasetPath, manifest.Files[0].RelativePath), "corruption");
        if (fault == "path")
        {
            manifest.Files[0].RelativePath = "../outside.parquet";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, json);
            await File.WriteAllBytesAsync(manifestPath, bytes);
            await File.WriteAllTextAsync(Path.Combine(f.DatasetPath, "_manifest.sha256"), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        var output = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "bad-snapshot");
        await Assert.ThrowsAsync<InvalidDataException>(() => ForexDatasetSnapshot.ExportAsync(f.DatasetPath, output, "EURUSD",
            ForexResearchFixture.Start, ForexResearchFixture.Start.AddMinutes(4)));
        Assert.False(File.Exists(Path.Combine(output, "_manifest.json")));
    }
}
