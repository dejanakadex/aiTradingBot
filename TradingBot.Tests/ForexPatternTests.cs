using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using Candle = TradingBot.Domain.Models.Candle;

namespace TradingBot.Tests;

public sealed class ForexPatternTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PatternType[] PricePatterns = [PatternType.Hammer, PatternType.ShootingStar, PatternType.BullishEngulfing,
        PatternType.BearishEngulfing, PatternType.DoubleBottom, PatternType.DoubleTop, PatternType.BreakoutAndRetest, PatternType.BreakdownAndRetest];

    [Theory]
    [InlineData(PatternType.Hammer)] [InlineData(PatternType.ShootingStar)]
    [InlineData(PatternType.BullishEngulfing)] [InlineData(PatternType.BearishEngulfing)]
    [InlineData(PatternType.DoubleBottom)] [InlineData(PatternType.DoubleTop)]
    [InlineData(PatternType.BreakoutAndRetest)] [InlineData(PatternType.BreakdownAndRetest)]
    public void ReusesGeometryWithoutNeutralVolumeAndRenormalizesQuality(PatternType type)
    {
        var stock = new PatternDetector(StockCompatibilityBaselineTests.FixtureOptions(), NullLogger<PatternDetector>.Instance)
            .Evaluate(new PatternDetectionInput { Candles = StockCompatibilityBaselineTests.PatternSeries(type), Directions = [TradeDirection.Long, TradeDirection.Short] })
            .Single(x => x.PatternType == type);
        var fx = Evaluate(Series(type)).Evaluations.Single(x => x.PatternType == type);
        Assert.True(fx.Accepted, string.Join(";", fx.Reasons));
        var volume = Assert.Single(fx.ScoreComponents, x => x.Code == "volumeScore");
        Assert.False(volume.IsAvailable); Assert.Equal(0m, volume.Weight); Assert.Equal(0m, volume.WeightedScore);
        Assert.Equal(1m, fx.ScoreComponents.Sum(x => x.Weight));
        var expected = stock.ScoreComponents.Where(x => x.Code != "volumeScore").Sum(x => x.WeightedScore) / 0.9m;
        Assert.Equal(decimal.Round(expected, 12), decimal.Round(fx.FinalScore, 12));
        Assert.Equal("unavailable", fx.Candidate!.Metadata["volumeScore"]);
        Assert.StartsWith(PipelineContractVersions.ForexPatterns, fx.PatternVersion);
        Assert.StartsWith("features-fx-midpoint-v1", fx.FeatureVersion);
    }

    [Fact]
    public void VwapAlwaysHasExplicitRejectionAndVolumeRequirementFailsClosed()
    {
        var profile = Profile(); profile.Geometry.Hammer.RequireVolumeConfirmation = true;
        var batch = Evaluate(Series(PatternType.Hammer), profile);
        Assert.All(batch.Evaluations.Where(x => x.PatternType is PatternType.VwapReclaim or PatternType.VwapReject), x =>
        {
            Assert.False(x.Accepted);
            Assert.Contains(x.HardConditions, c => c.Code == "tradeVolumeUnavailable" && !c.Passed);
        });
        Assert.Contains(batch.Evaluations.Single(x => x.PatternType == PatternType.Hammer).HardConditions,
            x => x.Code == "volumeConfirmationAvailable" && !x.Passed);
    }

    [Theory]
    [InlineData("quality")] [InlineData("context")] [InlineData("warmup")]
    public void ConfiguredGatesRejectEvenValidGeometry(string gate)
    {
        var profile = Profile();
        if (gate == "quality") profile.Geometry.MinimumTradeSetupQuality = 1m;
        if (gate == "context") profile.Geometry.Hammer.MinimumContextScore = 1m;
        if (gate == "warmup") profile.RequirePriceFeatureWarmup = true;
        var result = Evaluate(Series(PatternType.Hammer), profile).Evaluations.Single(x => x.PatternType == PatternType.Hammer);
        Assert.False(result.Accepted);
        Assert.Contains(result.HardConditions, x => !x.Passed && x.Code.StartsWith("fx", StringComparison.Ordinal));
    }

    [Fact]
    public void AvailabilityGapAndInputOrderAreDeterministic()
    {
        var candles = Series(PatternType.Hammer);
        Assert.Empty(Evaluate(candles, asOf: Start).Evaluations);
        var early = Evaluate(candles, asOf: candles[^1].ReceivedTimeUtc.AddTicks(-1));
        Assert.All(early.Evaluations, x => Assert.False(x.Accepted));
        Assert.Equal(Evaluate(candles).Evaluations.Select(x => x.FinalScore), Evaluate(candles.Reverse().ToArray()).Evaluations.Select(x => x.FinalScore));
        var gap = new[] { candles[0], Copy(candles[1], time: candles[1].TimestampUtc.AddSeconds(5)) };
        Assert.All(Evaluate(gap).Evaluations, x => Assert.Contains(x.HardConditions, c => c.Code == "fxConsecutiveWarmup" && !c.Passed));
        var future = Copy(candles[^1], time: Start.AddHours(2));
        Assert.Equal(Evaluate(candles).Evaluations.Select(x => x.FinalScore),
            Evaluate(candles.Append(future).ToArray(), asOf: candles[^1].ReceivedTimeUtc).Evaluations.Select(x => x.FinalScore));
    }

    [Theory]
    [InlineData("volume")] [InlineData("source")] [InlineData("instrument")] [InlineData("historical")]
    public void RejectsIncompatibleInputContracts(string kind)
    {
        var candles = Series(PatternType.Hammer);
        var x = candles[0];
        candles[0] = new Candle(x.Symbol, x.Timeframe, x.TimestampUtc, x.Open, x.High, x.Low, x.Close, kind == "volume" ? 0m : null,
            kind == "instrument" ? "FX-EURGBP" : x.InstrumentId, x.ReceivedTimeUtc,
            kind == "source" ? "FX.QuoteMidpoint.other.5s" : kind == "historical" ? "IBKR.Midpoint" : x.Source);
        Assert.Throws<ArgumentException>(() => Evaluate(candles));
    }

    [Fact]
    public void ConfigSnapshotVersionsAndMetadataPreserveFxPrecision()
    {
        var profile = Profile();
        var detector = new ForexPatternDetector(profile, new FeatureEngine(), NullLogger<PatternDetector>.Instance);
        var version = detector.Version;
        Assert.Equal(profile.Version, version);
        profile.Geometry.Hammer.MinPullbackPercent *= 2;
        Assert.NotEqual(version, profile.Version); Assert.Equal(version, detector.Version);
        var candles = Series(PatternType.Hammer).Select(x => new Candle(x.Symbol, x.Timeframe, x.TimestampUtc,
            x.Open + 0.0000123456789m, x.High + 0.0000123456789m, x.Low + 0.0000123456789m, x.Close + 0.0000123456789m,
            null, x.InstrumentId, x.ReceivedTimeUtc, x.Source)).ToArray();
        var evaluation = Evaluate(candles).Evaluations.Single(x => x.PatternType == PatternType.Hammer);
        Assert.Equal(candles[^1].Close, decimal.Parse(evaluation.Candidate!.Metadata["referencePrice"], CultureInfo.InvariantCulture));
        Assert.Contains(evaluation.PatternVersion.ToUpperInvariant(), evaluation.EvaluationKey);
        Assert.Equal(evaluation.EvaluationKey, evaluation.Candidate.PatternKey);
        Assert.Empty(new ForexPatternSettings().Validate());
        Assert.NotEmpty(new ForexPatternSettings { Profiles = [profile, profile] }.Validate());
        profile.Geometry.DoubleBottom.MinSeparation = 0; Assert.NotEmpty(profile.Validate());
    }

    [Fact]
    public async Task PersistsAcceptedAndRejectedVersionsIdempotentlyWithoutStockLabels()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var services = new ServiceCollection(); services.AddDbContextFactory<TradingBotDbContext>(x => x.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
        await using var db = await factory.CreateDbContextAsync(); await db.Database.EnsureCreatedAsync();
        var service = new CandidateResearchService(new UnusedDataset(), factory, new FixedClock(), Options.Create(new CandidateLabelingSettings()), NullLogger<CandidateResearchService>.Instance);
        var candles = Series(PatternType.Hammer);
        var batch = Evaluate(candles);
        Assert.Equal(10, await service.PersistBatchAsync(batch, "fx-test"));
        Assert.Equal(0, await service.PersistBatchAsync(batch, "fx-test"));
        var changed = Profile(); changed.Geometry.MinimumTradeSetupQuality = 1m;
        var other = Evaluate(candles, changed);
        Assert.Equal(10, await service.PersistBatchAsync(other, "fx-test"));
        Assert.Equal(0, await service.LabelMatureCandidatesAsync(Start.AddHours(3)));
        var records = await db.ResearchCandidateRecords.ToArrayAsync();
        Assert.Equal(20, records.Length); Assert.Empty(await db.CandidateLabelRecords.ToArrayAsync());
        Assert.Contains(records, x => x.Outcome == ResearchCandidateOutcome.Accepted);
        Assert.Contains(records, x => x.Outcome == ResearchCandidateOutcome.Rejected && x.ReasonsJson.Contains("VWAP"));
        Assert.All(records, x => Assert.Equal("labels-fx-deferred", x.LabelVersion));
        Assert.Contains(records, x => x.ScoreComponentsJson.Contains("\"isAvailable\":false"));
    }

    [Fact]
    public async Task PairedQuotePipelinePersistsResearchWithTradingDisabledAndIsolatesProfiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var services = new ServiceCollection(); services.AddDbContextFactory<TradingBotDbContext>(x => x.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
        await using var db = await factory.CreateDbContextAsync(); await db.Database.EnsureCreatedAsync();
        var trading = ForexCollectionTests.Settings();
        trading.Instruments[0].StrategyIds = ["fx-research", "fx-alternative"];
        Assert.False(trading.Instruments[0].TradingEnabled);
        var aggregates = new ShortAggregateSettings();
        var latest = new LatestMarketDataService(Options.Create(aggregates), Options.Create(trading));
        for (var i = 0; i < 28; i++)
        {
            decimal close = 1.1005m - i * 0.00001m;
            decimal[] prices = i == 27 ? [1.10023m, 1.10026m, 1.10000m, 1.10025m] : [close + 0.00001m, close + 0.00002m, close - 0.00001m, close];
            for (var j = 0; j < prices.Length; j++)
                foreach (var kind in new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask })
                    latest.Apply(new CanonicalMarketDataEvent
                    {
                        EventId = $"IBKR:FXQ:research:{i}-{j}:{kind.ToString().ToLowerInvariant()}", InstrumentId = "FX-EURUSD", Symbol = "EURUSD",
                        Kind = kind, EventTimeUtc = Start.AddSeconds(i * 5 + j + 1), ReceivedTimeUtc = Start.AddSeconds(i * 5 + j + 1.1),
                        Source = ForexQuoteAggregator.QuoteSource, Price = prices[j] + (kind == MarketDataEventKind.Bid ? -0.00001m : 0.00001m), Size = 100000m
                    });
        }
        var asOf = Start.AddSeconds(142); latest.AdvanceTo(asOf);
        var engine = new FeatureEngine();
        var inputs = new ForexFeatureService(latest, engine, Options.Create(trading), Options.Create(aggregates));
        var research = new CandidateResearchService(new UnusedDataset(), factory, new FixedClock(), Options.Create(new CandidateLabelingSettings()), NullLogger<CandidateResearchService>.Instance);
        var profile = new ForexPatternProfile { InstrumentId = "FX-EURUSD", StrategyId = "fx-research" };
        var other = profile.Snapshot(); other.StrategyId = "fx-alternative"; other.Geometry.MinimumTradeSetupQuality = 1m;
        var service = new ForexPatternResearchService(inputs, engine, research, Options.Create(new ForexPatternSettings { Profiles = [profile, other] }),
            Options.Create(trading), Options.Create(aggregates), NullLogger<PatternDetector>.Instance);
        var batch = await service.EvaluateAndPersistAsync("FX-EURUSD", "fx-research", 5, asOf, "fx-quote-test");
        Assert.Equal(10, batch.Evaluations.Count);
        Assert.All(batch.Evaluations, x => Assert.Contains(x.HardConditions, c => c.Code == "fxPriceFeatureWarmup" && c.Passed));
        var direct = new ForexPatternDetector(profile, engine, NullLogger<PatternDetector>.Instance).Evaluate(inputs.GetInput("FX-EURUSD", 5, asOf)!);
        Assert.Equal(direct.Evaluations.Select(x => (x.EvaluationKey, x.FinalScore, x.Accepted)), batch.Evaluations.Select(x => (x.EvaluationKey, x.FinalScore, x.Accepted)));
        await service.EvaluateAndPersistAsync("FX-EURUSD", "fx-research", 5, asOf, "fx-repeat");
        await service.EvaluateAndPersistAsync("FX-EURUSD", "fx-alternative", 5, asOf, "fx-alternative");
        Assert.Equal(20, await db.ResearchCandidateRecords.CountAsync());
        Assert.Empty(await db.CandidateLabelRecords.ToArrayAsync());
        Assert.Empty(latest.GetCompletedShortAggregates("FX-EURUSD", 5, asOf));
    }

    [Fact]
    public void ConfigurationBindingKeepsFxDefaultsAndDoesNotAppendDirections()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"ForexPatterns":{"Profiles":[{"InstrumentId":"FX-EURUSD","StrategyId":"fx-research","Directions":["Long"],
            "Geometry":{"Hammer":{"MinimumQualityForTradeSetup":0.75}}}]}}
            """));
        var config = new ConfigurationBuilder().AddJsonStream(json).Build();
        var settings = config.GetSection("ForexPatterns").Get<ForexPatternSettings>()!;
        Assert.Empty(settings.Validate());
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(TradeDirection.Long, Assert.Single(profile.Directions!));
        Assert.Equal(0.75m, profile.Geometry.Hammer.MinimumQualityForTradeSetup);
        Assert.Equal(0.0001m, profile.Geometry.Hammer.MinPullbackPercent);
        Assert.False(profile.Geometry.BullishEngulfing.RequireVolumeConfirmation);
        Assert.NotEqual(PatternDetectorVersion.Create(new PatternDetectorOptions()), profile.Version);
    }

    [Fact]
    public void PivotNecklineAndConfirmationThresholdsAreApplied()
    {
        var profile = Profile(); profile.Geometry.DoubleBottom.RequireNecklineBreakForTradeSetup = true;
        var result = Evaluate(Series(PatternType.DoubleBottom), profile).Evaluations.Single(x => x.PatternType == PatternType.DoubleBottom);
        Assert.False(result.Accepted); Assert.Contains(result.HardConditions, x => x.Code == "fxNecklineBreak" && !x.Passed);
        profile = Profile(); profile.Geometry.Hammer.MinimumConfirmationScore = 1m;
        result = Evaluate(Series(PatternType.Hammer), profile).Evaluations.Single(x => x.PatternType == PatternType.Hammer);
        Assert.False(result.Accepted); Assert.Contains(result.HardConditions, x => x.Code == "fxConfirmation" && !x.Passed);
    }

    private static ForexPatternProfile Profile()
    {
        var profile = new ForexPatternProfile { InstrumentId = "FX-EURUSD", StrategyId = "fx-research", MinimumConsecutiveCandles = 2,
            RequirePriceFeatureWarmup = false, EngulfingSupportProximityPercent = 0.004m, Geometry = StockCompatibilityBaselineTests.FixtureOptions() };
        profile.Geometry.MinimumTradeSetupQuality = 0m;
        foreach (var type in PricePatterns)
        {
            var rule = profile.Geometry.ForPattern(type);
            rule.RequireVolumeConfirmation = false; rule.MinimumQualityForTradeSetup = 0m; rule.MinimumContextScore = 0m; rule.MinimumConfirmationScore = 0m;
            if (rule is DoubleBottomPatternOptions pivot) pivot.RequireNecklineBreakForTradeSetup = false;
        }
        return profile;
    }

    private static Candle[] Series(PatternType type) => StockCompatibilityBaselineTests.PatternSeries(type).Select((x, i) =>
        new Candle("EURUSD", Timeframe.FiveSeconds, Start.AddSeconds((i + 1) * 5), x.Open / 10m, x.High / 10m, x.Low / 10m, x.Close / 10m,
            null, "FX-EURUSD", Start.AddSeconds((i + 1) * 5 + 2), "FX.QuoteMidpoint.test.5s")).ToArray();

    private static Candle Copy(Candle x, DateTime time) => new(x.Symbol, x.Timeframe, time, x.Open, x.High, x.Low, x.Close, null, x.InstrumentId, time.AddSeconds(2), x.Source);
    private static PatternDetectionBatch Evaluate(Candle[] candles, ForexPatternProfile? profile = null, DateTime? asOf = null) =>
        new ForexPatternDetector(profile ?? Profile(), new FeatureEngine(), NullLogger<PatternDetector>.Instance).Evaluate(
            new CanonicalFeatureInput { Candles = candles, ForexMidpoint = true, AsOfUtc = asOf ?? candles.Max(x => x.ReceivedTimeUtc) });

    private sealed class FixedClock : IClock { public DateTime UtcNow => Start.AddHours(3); }
    private sealed class UnusedDataset : IMarketDatasetStore
    {
        public Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(DatasetQuery query, CancellationToken cancellationToken = default) => throw new InvalidOperationException("FX-6 must not run stock labeling.");
        public Task<DatasetWriteResult> WriteBatchAsync(IReadOnlyCollection<MarketDatasetRecord> records, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
