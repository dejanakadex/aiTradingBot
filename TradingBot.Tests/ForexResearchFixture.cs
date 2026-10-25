using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests;

internal sealed class ForexResearchFixture : IDisposable, IDbContextFactory<TradingBotDbContext>, IClock
{
    public static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx10-" + Guid.NewGuid().ToString("N"));
    public DateTime UtcNow { get; set; } = Start.AddMinutes(5);
    public TradingSettings Trading = new() { Instruments = [new() { InstrumentId = "EURUSD", Symbol = "EURUSD", BaseCurrency = "EUR", Currency = "USD",
        SecurityType = "CASH", Exchange = "IDEALPRO", StrategyIds = ["patterns"], TradingEnabled = false, MaximumHoldingSeconds = 10 }] };
    public RiskSettings Limits = new() { MaximumPositionValue = 100000, MaximumRiskPerTrade = 1000, MaximumGrossExposure = 200000,
        MaximumNetExposure = 200000, MaximumInstrumentExposure = 100000, MaximumStrategyExposure = 100000,
        MaximumLeverage = 1, MaximumOpenPositions = 10, MaximumCorrelationGroupExposure = 100000 };
    public ForexRiskProfile Costs = new() { InstrumentId = "EURUSD", StrategyId = "patterns", MaximumCurrencyExposureUsd = 100000,
        SafetyBufferBps = 0, SlippagePerSidePips = 0, MinimumNetEdgeBps = 0 };
    public ForexResearchProfile Profile = new() { InstrumentId = "EURUSD", StrategyId = "patterns", HoldingSeconds = 10,
        MaximumBaseUnits = 20000, StartingEquityUsd = 100000, FillBaseUnitsPerQuote = 20000, FillAssumptionSource = "Synthetic test assumption",
        HistoricalRules = [new(new IbkrContractProbe("Confirmed", 120) { MatchCount = 1,
            Metadata = new(new("EUR", "CASH", "IDEALPRO", "USD"), "EUR.USD", "IDEALPRO", .00001m, 1000, 1000, 10000, "42", "UTC", null, null) }, Start,
            new(120, Start, "Synthetic test rules", 1000, 1000000, 1000, 42, [new(0, .00001m)]))] };
    public ShortAggregateSettings Aggregates = new();
    public ForexPatternSettings Patterns = new() { Profiles = [new() { InstrumentId = "EURUSD", StrategyId = "patterns" }] };
    public ForexSignalSettings Signals = new() { Profiles = [new() { Selection = new() { Enabled = true, InstrumentId = "EURUSD", StrategyId = "patterns",
        MinimumBars = 26, PatternTypes = [PatternType.Hammer], MinimumConfidence = 0, MaximumSignalAgeSeconds = 3, CooldownSeconds = 30 } }] };
    public FeatureEngine Features = new();
    public ParquetMarketDatasetStore Dataset;
    public string DatabasePath => Path.Combine(root, "state.db");
    public string DatasetPath => Path.Combine(root, "parquet");
    public string ConfigPath => Path.Combine(root, "config.json");
    public ForexResearchFixture()
    {
        Directory.CreateDirectory(root);
        using var db = CreateDbContext(); db.Database.EnsureCreated();
        var g = Patterns.Profiles[0].Geometry; g.MinimumTradeSetupQuality = 0; g.Hammer.MinimumQualityForTradeSetup = 0;
        g.Hammer.MinimumContextScore = 0; g.Hammer.MinimumConfirmationScore = 0;
        Signals.Profiles[0].ApprovedSignalVersion = Signals.Profiles[0].Version;
        Signals.Profiles[0].ApprovedPatternVersion = Patterns.Profiles[0].Version;
        Signals.Profiles[0].ApprovedFeatureVersion = Contract().Get("EURUSD", "patterns").Feature;
        Dataset = new(Options.Create(new DatasetStorageSettings { RootPath = Path.Combine(root, "parquet") }), this, NullLogger<ParquetMarketDatasetStore>.Instance);
    }
    public ForexResearchContract Contract() => new(Options.Create(new ForexResearchSettings { Profiles = [Profile] }), Options.Create(new ForexRiskSettings { Profiles = [Costs] }),
        Options.Create(Limits), Options.Create(Trading), Options.Create(Patterns), Options.Create(Signals), Options.Create(Aggregates), Options.Create(new CanonicalFeatureSettings()));
    public ForexTradeSimulator Simulator() => new(new(Options.Create(new ForexRiskSettings { Profiles = [Costs] }), Options.Create(Limits), Options.Create(Trading)));
    public CandidateResearchService Research() => new(Dataset, this, this, Options.Create(new CandidateLabelingSettings()), NullLogger<CandidateResearchService>.Instance);
    public DeterministicReplayService Replay() => new(Dataset, this, Features, new PatternDetectorFactory(Options.Create(new PatternDetectorOptions()), NullLoggerFactory.Instance),
        this, Options.Create(new ReplaySettings()), NullLogger<DeterministicReplayService>.Instance, Research(), Options.Create(Trading),
        shortAggregateSettings: Options.Create(Aggregates), forexContract: Contract(), forexSimulator: Simulator(), forexSignals: Options.Create(Signals), forexPatterns: Options.Create(Patterns));
    public TradingBotDbContext CreateDbContext() => new(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite($"Data Source={Path.Combine(root, "state.db")};Pooling=False").Options);
    public static ForexReplayQuote Q(int second, decimal bid = 1.10000m, decimal ask = 1.10002m) => new($"IBKR:FXQ:fx10:{second:D5}", Start.AddSeconds(second), Start.AddSeconds(second), bid, ask);
    public static MarketDatasetRecord[] Records()
    {
        var rows = new List<MarketDatasetRecord>();
        for (int i = 0; i < 38; i++)
        {
            var close = 1.1005m - Math.Min(i, 26) * .00001m;
            decimal[] prices = i >= 27 ? [1.10023m, 1.10026m, 1.10000m, 1.10025m] : [close + .00001m, close + .00002m, close - .00001m, close];
            for (int j = 0; j < 4; j++) foreach (var side in new[] { "bid", "ask" }) rows.Add(new()
            {
                SchemaVersion = "market-data-v1", EventId = $"IBKR:FXQ:fx10:{i:D5}-{j}:{side}", InstrumentId = "EURUSD", Symbol = "EURUSD", DataType = side,
                EventTimeUtc = Start.AddSeconds(i * 5 + j + 1), ReceivedTimeUtc = Start.AddSeconds(i * 5 + j + 1.1), Source = ForexQuoteAggregator.QuoteSource,
                IsFinal = true, CanPersist = true, QualityStatus = "Healthy", Price = prices[j] + (side == "bid" ? -.00001m : .00001m), Size = 999999
            });
        }
        return rows.ToArray();
    }
    public void Dispose()
    {
        // Only this fixture's fresh, absolute directory is ever removed.
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(root, true);
    }
}
