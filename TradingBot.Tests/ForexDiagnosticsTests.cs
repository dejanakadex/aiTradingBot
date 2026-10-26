using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog.Config;
using NLog.Targets;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexDiagnosticsTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ShortAggregateSettings Settings = new() { IntervalsSeconds = [5] };

    [Fact]
    public void CountersDistinguishTimeFailuresAndSurviveResetWithoutMutatingPriorSnapshot()
    {
        var latest = Latest();
        Pair(latest, "ok", 1, 1.1);
        Pair(latest, "future", 2, 1.9);
        Pair(latest, "stale", 3, 5.1);
        latest.AdvanceTo(Start.AddSeconds(7));
        var first = latest.GetForexDiagnostics("FX-EURUSD");
        Assert.Equal(4, first.RejectedQuoteEvents); Assert.Equal(2, first.FutureQuoteEvents); Assert.Equal(2, first.StaleQuoteEvents);
        Assert.Equal(new ForexWindowDiagnostics(0, 1), first.Windows[5]);
        latest.ResetForexAggregates("FX-EURUSD");
        Pair(latest, "next", 11, 11.1); latest.AdvanceTo(Start.AddSeconds(17));
        var next = latest.GetForexDiagnostics("FX-EURUSD");
        Assert.Equal(1, next.ResetCount); Assert.Equal(0, first.ResetCount);
        Assert.Equal(new ForexWindowDiagnostics(1, 1), next.Windows[5]);
        Assert.Equal(new ForexWindowDiagnostics(0, 1), first.Windows[5]);
    }

    [Fact]
    public void SummaryReadsWithoutFinalizingBarsAndReportsDeltasRatherThanRepeatingOldErrors()
    {
        var latest = Latest(); var logger = new CaptureLogger(); var service = Service(latest, logger);
        Pair(latest, "ok", 1, 1.1);
        service.Report(Start.AddSeconds(7));
        Assert.Empty(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)));
        latest.AdvanceTo(Start.AddSeconds(7));
        Pair(latest, "invalid", 11, 10.9);
        Pair(latest, "good", 12, 12.1); latest.AdvanceTo(Start.AddSeconds(17));
        Pair(latest, "after-gap", 21, 21.1); latest.AdvanceTo(Start.AddSeconds(27));
        service.Report(Start.AddSeconds(67));
        var warning = logger.Entries[^1];
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("FutureEventsDelta=2", warning.Text);
        Assert.Contains("\"CompletedDelta\":2", warning.Text);
        Assert.Contains("\"SuppressedDelta\":1", warning.Text);
        Assert.Contains("\"ConsecutiveBars\":1", warning.Text);
        Assert.Contains("\"EmptyIntervalsBeforeLastBar\":3", warning.Text);
        service.Report(Start.AddSeconds(68));
        Assert.Equal(LogLevel.Information, logger.Entries[^1].Level);
        Assert.Contains("FutureEventsDelta=0", logger.Entries[^1].Text);
        Assert.Contains("\"CompletedDelta\":0", logger.Entries[^1].Text);
    }

    [Fact]
    public void SummaryCountsConsecutiveBarsAndRetainsLastBarAgeWhenNoNewInputArrives()
    {
        var latest = Latest(); var logger = new CaptureLogger(); var service = Service(latest, logger);
        for (var i = 0; i < 30; i++) Pair(latest, i.ToString(), i * 5 + 1, i * 5 + 1.1);
        latest.AdvanceTo(Start.AddSeconds(152));
        service.Report(Start.AddSeconds(152));
        Assert.Contains("\"ConsecutiveBars\":30", logger.Entries[^1].Text);
        Assert.Contains("\"LastBarAgeSeconds\":2", logger.Entries[^1].Text);
        service.Report(Start.AddSeconds(212));
        Assert.Equal(LogLevel.Warning, logger.Entries[^1].Level);
        Assert.Contains("\"NoRecentBar\":true", logger.Entries[^1].Text);
        Assert.Contains("\"LastBarAgeSeconds\":62", logger.Entries[^1].Text);
        Assert.Contains("\"CompletedDelta\":0", logger.Entries[^1].Text);
    }

    [Fact]
    public void NLogDropsSqlInfoButPreservesBotInfoSqlWarningsAndBothErrorTargets()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TradingBot.Web", "nlog.config"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(Path.GetTempPath(), "fx-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new NLog.LogFactory();
            var config = new XmlLoggingConfiguration(Path.Combine(directory!.FullName, "TradingBot.Web", "nlog.config"), factory);
            Assert.IsType<FileTarget>(config.FindTargetByName("allfile")).FileName = Path.Combine(root, "all.log");
            Assert.IsType<FileTarget>(config.FindTargetByName("errorfile")).FileName = Path.Combine(root, "error.log");
            factory.Configuration = config;
            var sql = factory.GetLogger("Microsoft.EntityFrameworkCore.Database.Command");
            sql.Info("sql-info-marker"); sql.Warn("sql-warning-marker"); sql.Error("sql-error-marker");
            factory.GetLogger("TradingBot.Diagnostics").Info("bot-info-marker");
            factory.Flush();
            var all = File.ReadAllText(Path.Combine(root, "all.log")); var errors = File.ReadAllText(Path.Combine(root, "error.log"));
            Assert.DoesNotContain("sql-info-marker", all); Assert.Contains("bot-info-marker", all);
            Assert.Contains("sql-warning-marker", all); Assert.Contains("sql-error-marker", all);
            Assert.Contains("sql-error-marker", errors); Assert.DoesNotContain("sql-warning-marker", errors);
            Assert.DoesNotContain("bot-info-marker", errors);
        }
        finally { Directory.Delete(root, true); }
    }

    private static LatestMarketDataService Latest() => new(Options.Create(Settings), Options.Create(ForexCollectionTests.Settings()));
    private static ForexDiagnosticsHostedService Service(LatestMarketDataService latest, CaptureLogger logger) =>
        new(latest, new Connection(), Options.Create(ForexCollectionTests.Settings()), Options.Create(Settings), new SystemClock(), logger);
    private static void Pair(LatestMarketDataService latest, string id, double at, double received)
    {
        foreach (var kind in new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask })
            latest.Apply(new CanonicalMarketDataEvent { EventId = $"IBKR:FXQ:test:{id}:{kind.ToString().ToLowerInvariant()}",
                InstrumentId = "FX-EURUSD", Symbol = "EURUSD", Source = "IBKR.TickByTick.Forex.BidAsk.v2", Kind = kind,
                Price = kind == MarketDataEventKind.Bid ? 1.1m : 1.1002m, IsFinal = true,
                EventTimeUtc = Start.AddSeconds(at), ReceivedTimeUtc = Start.AddSeconds(received) });
    }
    private sealed class Connection : IIbkrConnectionService
    {
        public ConnectionStatus Status => ConnectionStatus.Connected;
        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged { add { } remove { } }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Diagnostics must not connect.");
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Diagnostics must not disconnect.");
    }
    private sealed class CaptureLogger : ILogger<ForexDiagnosticsHostedService>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
