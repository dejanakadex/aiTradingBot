using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class ForexSignalTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ApprovedProfileEmitsOnlyShadowAndPersistsCompleteAudit()
    {
        using var fixture = new Fixture(); fixture.Feed(); fixture.Approve();
        var output = await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now);
        var signal = Assert.Single(output);
        Assert.Equal("true", signal.Metadata["shadowOnly"]);
        Assert.Contains(fixture.Signals.Profiles[0].Version, signal.PatternVersion);
        Assert.Equal(fixture.Signals.Profiles[0].Version, signal.Context.StrategyVersion);
        await using var db = fixture.CreateDbContext();
        var records = await db.ResearchCandidateRecords.ToArrayAsync();
        Assert.Equal(280, records.Length); // Includes warmup/stale rejections for the full available history.
        var accepted = Assert.Single(records, x => x.DecisionStage == ForexSignalTriggerService.AcceptedStage);
        Assert.Equal(ResearchCandidateOutcome.Accepted, accepted.Outcome);
        Assert.Contains("signalProfile", accepted.MetadataJson);
        Assert.Contains(records, x => x.Outcome == ResearchCandidateOutcome.Rejected && x.ReasonsJson.Contains("VWAP"));
        Assert.Empty(await db.CandidateLabelRecords.ToArrayAsync());
        Assert.False(fixture.Bus.PatternCandidateReader.TryRead(out _));
        Assert.False(fixture.Bus.TradeSignalReader.TryRead(out _));
        Assert.False(fixture.Bus.OrderRequestReader.TryRead(out _));
    }

    [Theory]
    [InlineData("approval", "approval")]
    [InlineData("spread", "spread")]
    [InlineData("quote-age", "stale")]
    [InlineData("signal-age", "maximum age")]
    [InlineData("volatility", "ATR/price")]
    [InlineData("direction", "Direction")]
    [InlineData("confidence", "confidence")]
    public async Task ProfileGatesPersistSpecificBlockReasons(string gate, string reason)
    {
        using var fixture = new Fixture(); fixture.Feed();
        var profile = fixture.Signals.Profiles[0];
        switch (gate)
        {
            case "spread": profile.MaximumSpreadBps = 0.01m; break;
            case "quote-age": profile.MaximumQuoteAgeSeconds = 1; break;
            case "signal-age": profile.Selection.MaximumSignalAgeSeconds = 1; break;
            case "volatility": profile.MaximumAtrToPriceRatio = 0.0000001m; break;
            case "direction": fixture.Trading.Instruments[0].AllowedDirections = [TradeDirection.Short]; break;
            case "confidence": profile.Selection.MinimumConfidence = 1m; break;
        }
        if (gate != "approval") fixture.Approve();
        Assert.Empty(await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now));
        await using var db = fixture.CreateDbContext();
        var end = Start.AddSeconds(140).ToString("O");
        var hammer = await db.ResearchCandidateRecords.SingleAsync(x => x.PatternType == PatternType.Hammer && x.SourceEventId.EndsWith(end));
        Assert.Equal(ResearchCandidateOutcome.Blocked, hammer.Outcome);
        Assert.Contains(reason, hammer.ReasonsJson, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("PatternDeduplication", hammer.DecisionStage);
    }

    [Theory]
    [InlineData("signal")] [InlineData("pattern")] [InlineData("features")]
    public async Task ChangedVersionsInvalidateApproval(string changed)
    {
        using var fixture = new Fixture(); fixture.Feed(); fixture.Approve();
        if (changed == "signal") fixture.Signals.Profiles[0].MaximumSpreadBps *= 2;
        if (changed == "pattern") fixture.Patterns.Profiles[0].Geometry.Hammer.MinPullbackPercent *= 2;
        if (changed == "features") fixture.Features = new FeatureEngine(emaShort: 10);
        Assert.Empty(await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now));
        await using var db = fixture.CreateDbContext();
        Assert.Contains(await db.ResearchCandidateRecords.ToArrayAsync(), x => x.ReasonsJson.Contains("approval"));
    }

    [Fact]
    public async Task CooldownSurvivesRestartAndConcurrentRepeatedCallsStayIdempotent()
    {
        using var fixture = new Fixture(); fixture.Feed(); fixture.Approve();
        var trigger = fixture.Trigger();
        var results = await Task.WhenAll(trigger.ProcessAsync("FX-EURUSD", fixture.Now), trigger.ProcessAsync("FX-EURUSD", fixture.Now));
        Assert.Single(results.SelectMany(x => x));
        fixture.Feed(28, 1); fixture.Now = Start.AddSeconds(147);
        Assert.Empty(await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(290, await db.ResearchCandidateRecords.CountAsync());
        Assert.Contains(await db.ResearchCandidateRecords.ToArrayAsync(), x => x.ReasonsJson.Contains("cooldown"));
        fixture.Feed(29, 6); fixture.Now = Start.AddSeconds(177);
        Assert.Single(await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now));
        Assert.Equal(2, await db.ResearchCandidateRecords.CountAsync(x => x.DecisionStage == ForexSignalTriggerService.AcceptedStage));
    }

    [Fact]
    public async Task SameStrategyCodeAndIndependentApprovalsWorkBesideSpy()
    {
        using var fixture = new Fixture();
        fixture.Trading.Instruments = [fixture.Trading.Instruments[0], new InstrumentSettings
        { InstrumentId = "FX-EURGBP", BaseCurrency = "EUR", Currency = "GBP", SecurityType = "CASH", Exchange = "IDEALPRO", StrategyIds = ["alpha"], TradingEnabled = false },
            new InstrumentSettings { InstrumentId = "SPY", Symbol = "SPY", StrategyIds = ["alpha"] }];
        fixture.ResetLatest();
        var otherPattern = fixture.Patterns.Profiles[0].Snapshot(); otherPattern.InstrumentId = "FX-EURGBP";
        var otherSignal = fixture.Signals.Profiles[0].Snapshot(); otherSignal.Selection.InstrumentId = "FX-EURGBP";
        fixture.Patterns.Profiles = [fixture.Patterns.Profiles[0], otherPattern];
        fixture.Signals.Profiles = [fixture.Signals.Profiles[0], otherSignal];
        fixture.Feed(); fixture.Feed(instrument: "FX-EURGBP"); fixture.Feed(instrument: "SPY"); fixture.Approve();
        var stock = new ShortSignalTriggerService(Options.Create(new ShortSignalSettings { Profiles = [new()
            { InstrumentId = "SPY", StrategyId = "alpha", PatternTypes = [PatternType.Hammer], MinimumConfidence = 0, MinimumBars = 26, MaximumSignalAgeSeconds = 3 }] }),
            Options.Create(fixture.Trading), Options.Create(fixture.Aggregates), fixture.Latest,
            new PatternDetectorFactory(Options.Create(new PatternDetectorOptions()), NullLoggerFactory.Instance), fixture.Features, fixture.Bus, NullLogger<ShortSignalTriggerService>.Instance);
        var trigger = fixture.Trigger();
        await stock.ProcessAsync(fixture.Trading.GetEnabledInstruments().Single(x => x.InstrumentId == "SPY"), fixture.Now);
        Assert.Single(await trigger.ProcessAsync("FX-EURUSD", fixture.Now));
        Assert.Empty(await trigger.ProcessAsync("FX-EURGBP", fixture.Now)); // EUR/USD approval does not cover another pair.
        Assert.True(fixture.Bus.PatternCandidateReader.TryRead(out var spy)); Assert.Equal("SPY", spy!.InstrumentId);
        Assert.False(fixture.Bus.PatternCandidateReader.TryRead(out _));
        fixture.Approve(1); fixture.Feed(28, 1, "FX-EURGBP"); fixture.Now = Start.AddSeconds(147);
        Assert.Single(await fixture.Trigger().ProcessAsync("FX-EURGBP", fixture.Now)); // EUR/USD cooldown does not block EUR/GBP.
    }

    [Fact]
    public async Task GapResetsWarmupAndFutureFramesDoNotLeakIntoAnEarlierDecision()
    {
        using var fixture = new Fixture(); fixture.Feed(); fixture.Approve();
        Assert.Empty(await fixture.Trigger().ProcessAsync("FX-EURUSD", Start.AddSeconds(141.9)));
        fixture.Feed(30, 1); fixture.Now = Start.AddSeconds(157);
        Assert.Empty(await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now));
        await using var db = fixture.CreateDbContext();
        var latest = (await db.ResearchCandidateRecords.ToArrayAsync()).Where(x => x.SourceEventId.Contains(Start.AddSeconds(155).ToString("O"))).ToArray();
        Assert.Equal(10, latest.Length); Assert.All(latest, x => Assert.Contains("warmup", x.ReasonsJson, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostedTimerClosesBarsWithTradingDisabledButSkipsDisconnectedFeed()
    {
        using var fixture = new Fixture(); fixture.Feed(advance: false);
        fixture.Latest.AdvanceTo(fixture.Now); fixture.Approve();
        var connection = new Connection();
        var host = new ForexSignalHostedService(fixture.Trigger(), fixture.Latest, Options.Create(fixture.Signals), connection,
            fixture, NullLogger<ForexSignalHostedService>.Instance);
        await host.RunOnceAsync(fixture.Now);
        await using var db = fixture.CreateDbContext(); Assert.Equal(0, await db.ResearchCandidateRecords.CountAsync());
        connection.Status = ConnectionStatus.Connected;
        await host.RunOnceAsync(fixture.Now);
        Assert.Single(await db.ResearchCandidateRecords.Where(x => x.DecisionStage == ForexSignalTriggerService.AcceptedStage).ToArrayAsync());
        Assert.False(fixture.Trading.Instruments[0].TradingEnabled);
        fixture.Feed(28, 6, advance: false); fixture.Now = Start.AddSeconds(172);
        await host.RunOnceAsync(fixture.Now); // Timer, not another tick, closes the last bar at +2s.
        Assert.Equal(2, await db.ResearchCandidateRecords.CountAsync(x => x.DecisionStage == ForexSignalTriggerService.AcceptedStage));
    }

    [Fact]
    public void ConfigurationRejectsStockRoutingAndKeepsApprovalsExplicit()
    {
        using var fixture = new Fixture();
        Assert.NotEmpty(new ShortSignalSettings { Profiles = [fixture.Signals.Profiles[0].Selection] }.GetValidationErrors(fixture.Trading, fixture.Aggregates));
        fixture.Signals.Profiles[0].Selection.InstrumentId = "SPY";
        Assert.NotEmpty(fixture.Signals.Validate(fixture.Trading, fixture.Aggregates, fixture.Patterns));
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"ForexSignals":{"Profiles":[{"Selection":{"Enabled":true,"InstrumentId":"FX-EURUSD","StrategyId":"alpha","PatternTypes":["Hammer"]}}]}}
            """));
        var settings = new ConfigurationBuilder().AddJsonStream(json).Build().GetSection("ForexSignals").Get<ForexSignalSettings>()!;
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(PatternType.Hammer, Assert.Single(profile.Selection.PatternTypes));
        Assert.Null(profile.ApprovedSignalVersion);
        Assert.Equal(profile.Version, profile.Snapshot().Version);
    }

    [Fact]
    public async Task PersistenceFailureRetriesWithoutBlockingAnotherStrategy()
    {
        using var fixture = new Fixture();
        fixture.Trading.Instruments[0].StrategyIds = ["alpha", "beta"];
        var pattern = fixture.Patterns.Profiles[0].Snapshot(); pattern.StrategyId = "beta";
        var signal = fixture.Signals.Profiles[0].Snapshot(); signal.Selection.StrategyId = "beta";
        fixture.Patterns.Profiles = [fixture.Patterns.Profiles[0], pattern]; fixture.Signals.Profiles = [fixture.Signals.Profiles[0], signal];
        fixture.Feed(); fixture.Approve(); fixture.Approve(1);
        var trigger = fixture.Trigger();
        fixture.FailOnDbCreation = fixture.DbCreations + 2; // Profile read succeeds; first research transaction cannot open.
        var first = Assert.Single(await trigger.ProcessAsync("FX-EURUSD", fixture.Now)); Assert.Equal("beta", first.StrategyId);
        var retry = Assert.Single(await trigger.ProcessAsync("FX-EURUSD", fixture.Now)); Assert.Equal("alpha", retry.StrategyId);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(560, await db.ResearchCandidateRecords.CountAsync());
        Assert.Equal(2, await db.ResearchCandidateRecords.CountAsync(x => x.DecisionStage == ForexSignalTriggerService.AcceptedStage));
    }

    [Fact]
    public async Task FiveAndFifteenSecondProfilesHaveIndependentCooldownAndVersions()
    {
        using var fixture = new Fixture();
        var pattern = fixture.Patterns.Profiles[0].Snapshot(); pattern.IntervalSeconds = 15;
        var signal = fixture.Signals.Profiles[0].Snapshot(); signal.Selection.IntervalSeconds = 15;
        fixture.Patterns.Profiles = [fixture.Patterns.Profiles[0], pattern]; fixture.Signals.Profiles = [fixture.Signals.Profiles[0], signal];
        fixture.Feed(count: 84); fixture.Now = Start.AddSeconds(422); fixture.Approve(); fixture.Approve(1);
        var results = await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, x => x.Timeframe == Timeframe.FiveSeconds);
        Assert.Contains(results, x => x.Timeframe == Timeframe.FifteenSeconds);
        Assert.Equal(2, results.Select(x => x.PatternVersion).Distinct().Count());
        Assert.Empty(await fixture.Trigger().ProcessAsync("FX-EURUSD", fixture.Now));
    }

    private sealed class Fixture : IDisposable, IDbContextFactory<TradingBotDbContext>, IClock
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"fx7-{Guid.NewGuid():N}.db");
        public TradingSettings Trading = new() { Instruments = [new() { InstrumentId = "FX-EURUSD", BaseCurrency = "EUR", Currency = "USD",
            SecurityType = "CASH", Exchange = "IDEALPRO", StrategyIds = ["alpha"], TradingEnabled = false }] };
        public ShortAggregateSettings Aggregates = new();
        public ForexPatternSettings Patterns = new() { Profiles = [new() { InstrumentId = "FX-EURUSD", StrategyId = "alpha" }] };
        public ForexSignalSettings Signals = new() { Profiles = [new() { Selection = new() { Enabled = true, InstrumentId = "FX-EURUSD", StrategyId = "alpha",
            MinimumBars = 26, PatternTypes = [PatternType.Hammer], MinimumConfidence = 0m, MaximumSignalAgeSeconds = 3, CooldownSeconds = 30 } }] };
        public FeatureEngine Features = new();
        public LatestMarketDataService Latest = null!;
        public TradingEventBus Bus = new(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
        public DateTime Now = Start.AddSeconds(142);
        public int DbCreations;
        public int FailOnDbCreation;
        public DateTime UtcNow => Now;
        public Fixture()
        {
            using var db = CreateDbContext(); db.Database.EnsureCreated(); ResetLatest();
            var geometry = Patterns.Profiles[0].Geometry;
            geometry.MinimumTradeSetupQuality = 0m; geometry.Hammer.MinimumQualityForTradeSetup = 0m;
            geometry.Hammer.MinimumContextScore = 0m; geometry.Hammer.MinimumConfirmationScore = 0m;
        }
        public TradingBotDbContext CreateDbContext()
        {
            if (++DbCreations == FailOnDbCreation) throw new IOException("Simulated research persistence outage.");
            return new(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options);
        }
        public void ResetLatest() => Latest = new(Options.Create(Aggregates), Options.Create(Trading));
        public ForexFeatureService Inputs() => new(Latest, Features, Options.Create(Trading), Options.Create(Aggregates));
        public ForexSignalTriggerService Trigger() => new(Options.Create(Signals), Options.Create(Patterns), Options.Create(Trading), Options.Create(Aggregates),
            Latest, Inputs(), Features, new CandidateResearchService(new NoDataset(), this, this, Options.Create(new CandidateLabelingSettings()),
                NullLogger<CandidateResearchService>.Instance), this, NullLoggerFactory.Instance);
        public void Approve(int index = 0)
        {
            var profile = Signals.Profiles[index];
            profile.ApprovedSignalVersion = profile.Version;
            profile.ApprovedPatternVersion = Patterns.Profiles[index].Version;
            profile.ApprovedFeatureVersion = Inputs().Compute(profile.Selection.InstrumentId, profile.Selection.IntervalSeconds, Now)!.FeatureVersion;
        }
        public void Feed(int first = 0, int count = 28, string instrument = "FX-EURUSD", bool advance = true)
        {
            for (int i = first; i < first + count; i++)
            {
                decimal close = 1.1005m - Math.Min(i, 26) * 0.00001m;
                decimal[] prices = i >= 27 ? [1.10023m, 1.10026m, 1.10000m, 1.10025m] : [close + 0.00001m, close + 0.00002m, close - 0.00001m, close];
                for (int j = 0; j < prices.Length; j++)
                    foreach (var kind in instrument == "SPY" ? new[] { MarketDataEventKind.Trade } : [MarketDataEventKind.Bid, MarketDataEventKind.Ask])
                        Latest.Apply(new CanonicalMarketDataEvent
                        {
                            EventId = $"IBKR:FXQ:fx7:{instrument}-{i}-{j}:{kind.ToString().ToLowerInvariant()}", InstrumentId = instrument,
                            Symbol = instrument == "SPY" ? "SPY" : instrument.Replace("FX-", ""), Kind = kind,
                            EventTimeUtc = Start.AddSeconds(i * 5 + j + 1), ReceivedTimeUtc = Start.AddSeconds(i * 5 + j + 1.1),
                            Source = instrument == "SPY" ? "Test.Tick" : ForexQuoteAggregator.QuoteSource,
                            Price = prices[j] + (kind == MarketDataEventKind.Bid ? -0.00001m : kind == MarketDataEventKind.Ask ? 0.00001m : 0m), Size = 100000m
                        });
            }
            if (advance) Latest.AdvanceTo(Start.AddSeconds((first + count) * 5 + 2));
        }
        public void Dispose() { Bus.Dispose(); File.Delete(_path); }
    }
    private sealed class Connection : IIbkrConnectionService
    {
        public ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;
        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("FX-7 must not connect a broker.");
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class NoDataset : IMarketDatasetStore
    {
        public Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(DatasetQuery query, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<DatasetWriteResult> WriteBatchAsync(IReadOnlyCollection<MarketDatasetRecord> records, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
