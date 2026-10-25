using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.ShadowProbe;

/// <summary>Runs only the existing collection services, with an order-blocked adapter and a fresh data directory.</summary>
public static class ForexDataProbeRunner
{
    public static async Task<int> RunAsync(string configPath, string outputPath, int port, int clientId, int seconds, bool shadow = false)
    {
        if (port is not (7497 or 4002) || clientId < 0 || seconds is < 5 or > 3600) return 2;
        TradingSettings trading;
        try { trading = ContractProbeRunner.ReadConfiguration(await File.ReadAllTextAsync(configPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Invalid FX data config ({ex.GetType().Name}); no broker connection opened.");
            return 2;
        }
        var enabled = trading.GetEnabledInstruments();
        if (enabled.Count != 1 || enabled[0].InstrumentType != InstrumentType.Forex)
        {
            Console.Error.WriteLine("Data probe requires exactly one enabled FX instrument; disable other entries in its separate config.");
            return 2;
        }
        var instrument = enabled[0];
        var output = Path.GetFullPath(outputPath);
        var root = Path.Combine(Path.GetDirectoryName(output)!, $"fx4-data-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var clock = new SystemClock();
        var started = clock.UtcNow;
        var factory = new ProbeDbFactory(new DbContextOptionsBuilder<TradingBotDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "collection.sqlite")}").Options);
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
        var collectionOptions = Options.Create(new MarketDataCollectionSettings { MonitorIntervalSeconds = 1 });
        var tradingOptions = Options.Create(trading);
        var datasetOptions = Options.Create(new DatasetStorageSettings { RootPath = Path.Combine(root, "parquet"), BatchSize = 100, FlushIntervalSeconds = 1 });
        var store = new ParquetMarketDatasetStore(datasetOptions, clock, NullLogger<ParquetMarketDatasetStore>.Instance);
        using var writer = new MarketDatasetWriterHostedService(store, datasetOptions, NullLogger<MarketDatasetWriterHostedService>.Instance);
        using var broker = new IbkrBrokerService(new IbkrOptions { Port = port, ClientId = clientId, ReadOnlyDiagnostics = true },
            NullLogger<IbkrBrokerService>.Instance, collectionOptions, tradingOptions);
        var connection = new ProbeConnection(broker);
        var registry = new InstrumentRegistryService(factory, tradingOptions, clock);
        await registry.SynchronizeConfiguredAsync();
        var backfill = new HistoricalBackfillService(factory, broker, registry, Options.Create(new HistoricalBackfillSettings()),
            clock, NullLogger<HistoricalBackfillService>.Instance, writer);
        var latest = new LatestMarketDataService(trading: tradingOptions);
        ForexProbeAnalysis? analysis = null;
        if (shadow)
        {
            try { analysis = new ForexProbeAnalysis(configPath, tradingOptions, latest, factory, store, clock, connection); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.InvalidDataException)
            { Console.Error.WriteLine($"Invalid FX shadow configuration ({ex.GetType().Name}); no broker connection opened."); return 2; }
        }
        var quality = new MarketDataQualityService(factory, tradingOptions, clock, NullLogger<MarketDataQualityService>.Instance);
        var status = new MarketDataCollectionStatusService();
        using var bus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
        using var pipeline = new MarketDataPipeline(broker, bus, factory, new MarketDataValidator(tradingOptions, clock), null,
            NullLogger<MarketDataPipeline>.Instance, qualityService: quality, latestMarketData: latest,
            settings: tradingOptions, clock: clock, datasetSink: writer);
        using var ticks = new TickMarketDataHostedService([broker], connection, pipeline, status, quality, latest,
            factory, tradingOptions, collectionOptions, clock, NullLogger<TickMarketDataHostedService>.Instance);
        using var bars = new MarketDataSubscriptionHostedService(broker, pipeline, connection, backfill,
            new CandleHistoryService(factory), status, tradingOptions, collectionOptions, clock,
            NullLogger<MarketDataSubscriptionHostedService>.Instance);
        var reasons = new List<string>();
        HistoricalGapFillResult? history = null;
        int? server = null;
        int? marketDataErrorCode = null;
        var end = new DateTime(started.Ticks - started.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        while (!MarketDataSession.IsForexOpen(end.AddMinutes(-1))) end = end.AddMinutes(-1);
        var start = end.AddHours(-1);
        await writer.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 90));
            await broker.StartAsync(timeout.Token);
            server = broker.ServerVersion;
            var contract = await broker.ProbeContractAsync(instrument, timeout.Token);
            if (contract.Status != "Confirmed")
            {
                reasons.Add($"Contract status={contract.Status}; errorCode={contract.ErrorCode}.");
                throw new InvalidOperationException("Contract was not confirmed.");
            }
            history = await backfill.FillGapAsync(instrument.InstrumentId, instrument.Symbol, "1m", start, end, timeout.Token);
            await ticks.StartAsync(timeout.Token);
            await bars.StartAsync(timeout.Token);
            var until = clock.UtcNow.AddSeconds(seconds);
            while (clock.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
                if (analysis != null) await analysis.RunAsync(timeout.Token);
            }
        }
        catch (Exception ex) { reasons.Add($"Read-only data probe failed ({ex.GetType().Name})."); }
        finally
        {
            await ticks.StopAsync(CancellationToken.None);
            await bars.StopAsync(CancellationToken.None);
            marketDataErrorCode = broker.GetMarketDataErrorCode(instrument.Symbol);
            await broker.StopAsync();
            await writer.StopAsync(CancellationToken.None);
        }
        var verification = await store.VerifyAsync();
        var records = await store.ReadAsync(new DatasetQuery(instrument.InstrumentId));
        await using var check = factory.CreateDbContext();
        var candleCount = await check.Candles.CountAsync();
        var bidCount = records.Count(x => x.DataType == "bid");
        var askCount = records.Count(x => x.DataType == "ask");
        var validBars = records.Where(x => x.DataType == "bar" && x.CanPersist).ToArray();
        var historyPassed = history?.Success == true && history.BarsInserted > 0 && verification.IsValid && bus.CandleReader.Count == 0
            && candleCount >= history.BarsInserted && validBars.Length >= history.BarsInserted
            && validBars.All(x => x.Volume is null && x.Source.StartsWith("IBKR.Midpoint", StringComparison.Ordinal));
        var shadowDataObserved = shadow && bidCount > 0 && askCount > 0
            && await check.ResearchCandidateRecords.AnyAsync(x => x.ReplayRunId == null);
        var report = new
        {
            StartedUtc = started, EndedUtc = clock.UtcNow, ApiVersion = broker.ApiVersion, ServerVersion = server,
            InstrumentId = instrument.InstrumentId, instrument.Symbol, HistoryStartUtc = start, HistoryEndUtc = end,
            HistoryAndStoragePassed = historyPassed, HistoryBars = history?.BarsInserted ?? 0, SqliteCandles = candleCount,
            ParquetRows = verification.RowCount, verification.ManifestSha256, verification.IsValid,
            BidTicks = bidCount, AskTicks = askCount, TradeTicks = records.Count(x => x.DataType == "trade"),
            LiveQuotesConfirmed = bidCount > 0 && askCount > 0 && records.Where(x => x.DataType is "bid" or "ask").All(x => x.CanPersist),
            SessionOpenAtStart = MarketDataSession.IsForexOpen(started), MarketDataErrorCode = marketDataErrorCode,
            TickCoverageGaps = await check.TickCoverageGapRecords.CountAsync(), Reasons = reasons,
            ForexFiveSecondBars = latest.GetCompletedForexAggregates(instrument.InstrumentId, 5, clock.UtcNow).Count,
            ForexFifteenSecondBars = latest.GetCompletedForexAggregates(instrument.InstrumentId, 15, clock.UtcNow).Count,
            ForexAggregateDiagnostics = latest.GetForexDiagnostics(instrument.InstrumentId),
            ForexFiveSecondFeatures = new ForexFeatureService(latest, new FeatureEngine(), tradingOptions,
                Options.Create(new ShortAggregateSettings())).Compute(instrument.InstrumentId, 5, clock.UtcNow),
            PublishedAnalysisCandles = bus.CandleReader.Count,
            ShadowAnalysis = analysis == null ? null : await analysis.ReportAsync(),
            ShadowDataObserved = shadowDataObserved,
            ShadowPendingReason = shadow && !shadowDataObserved ? "No live quote-to-pattern sample; check session, tick availability and warmup. History success is not shadow success." : null,
            Scope = shadow ? "AnalysisOnly collectors, FX features and pattern gates in a new database. No trained model is imported, no orders; model readiness cannot pass in this probe."
                : "Read-only production collectors and bounded historical MIDPOINT sample; no orders or strategy validation."
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"FX data report: {output}; isolated SQLite/Parquet: {root}");
        Console.WriteLine($"History/storage={historyPassed}; bars={report.HistoryBars}; bid={bidCount}; ask={askCount}; sessionOpen={report.SessionOpenAtStart}.");
        if (!historyPassed || reasons.Count > 0) return 1;
        return shadow && !shadowDataObserved ? 3 : 0;
    }

    private sealed class ProbeDbFactory(DbContextOptions<TradingBotDbContext> options) : IDbContextFactory<TradingBotDbContext>
    {
        public TradingBotDbContext CreateDbContext() => new(options);
    }
    private sealed class ProbeConnection(IbkrBrokerService broker) : IIbkrConnectionService
    {
        public ConnectionStatus Status => broker.IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged
        { add => broker.ConnectionStatusChanged += value; remove => broker.ConnectionStatusChanged -= value; }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => broker.StartAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => broker.StopAsync(cancellationToken);
    }
}
