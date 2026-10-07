using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.ShadowProbe;

namespace TradingBot.Tests;

public sealed class ShadowPerformanceTests
{
    [Fact]
    public void FeedAccumulator_UsesPredeclaredThresholdsAndFlagsDroppedOrOutOfOrderTicks()
    {
        var start = new DateTime(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc);
        var accumulator = new ShadowFeedAccumulator("US-STK-SPY-SMART", "SPY");
        accumulator.Add(Tick(MarketDataEventKind.Trade, start, 100), 3);
        accumulator.Add(Tick(MarketDataEventKind.Bid, start, 200), 1);
        accumulator.Add(Tick(MarketDataEventKind.Ask, start, 300), 2);
        var thresholds = new ShadowFeedThresholds(10, 1, 1, 350, 0);
        var healthy = accumulator.Finish(0, null, thresholds);
        Assert.True(healthy.Passed);
        Assert.Equal(3, healthy.PeakQueueDepth);
        Assert.Equal(300d, healthy.P95ReceiveLagMilliseconds);
        accumulator.Add(Tick(MarketDataEventKind.Trade, start.AddSeconds(-1), 100), 0);
        var unhealthy = accumulator.Finish(1, 10089, thresholds);
        Assert.False(unhealthy.Passed);
        Assert.Equal(1, unhealthy.OutOfOrderEvents);
        Assert.Contains(unhealthy.Reasons, x => x.Contains("dropped", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(unhealthy.Reasons, x => x.Contains("10089"));
    }

    [Fact]
    public async Task DecisionAssessment_RequiresRiskAuditAndComparesApprovedModelToBaseline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shadow30-{Guid.NewGuid():N}.db");
        var start = new DateTime(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc);
        var signalId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using (var db = new TradingBotDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.ScalpingExecutionDecisionRecords.Add(new ScalpingExecutionDecisionRecord
                {
                    SignalId = signalId, InstrumentId = "US-STK-SPY-SMART", StrategyId = "a", Symbol = "SPY",
                    EvaluatedAtUtc = start.AddMinutes(2), QuoteAsOfUtc = start.AddMinutes(2).AddMilliseconds(-100),
                    Approved = true, ExpectedNetEdgeBps = 5m
                });
                db.AiAnalysisRecords.Add(new AiAnalysisRecord
                {
                    Context = "ShadowPatternTiming", TimestampUtc = start.AddMinutes(2),
                    ResultJson = JsonSerializer.Serialize(new
                    {
                        SignalId = signalId,
                        InstrumentId = "US-STK-SPY-SMART", StrategyId = "a", signalAgeMilliseconds = 200,
                        processingMilliseconds = 50, pendingPatternQueueDepth = 2
                    })
                });
                db.NumericalModelRecords.Add(new NumericalModelRecord
                {
                    Id = Guid.NewGuid(), InstrumentId = "US-STK-SPY-SMART", StrategyId = "a",
                    Status = NumericalModelStatus.Approved,
                    BaselineHoldoutJson = JsonSerializer.Serialize(new NumericalModelMetrics(100, 10, 0.5m, 1m, 10m, 3m)),
                    CandidateHoldoutJson = JsonSerializer.Serialize(new NumericalModelMetrics(100, 10, 0.6m, 2m, 20m, 2m))
                });
                await db.SaveChangesAsync();
            }
            var limits = new ShadowPerformanceSettings
            {
                MinimumDecisionsPerStrategy = 1, MinimumObservationMinutes = 1
            };
            var missingRisk = await ShadowDecisionAssessor.AssessAsync(path, start, start.AddMinutes(5), limits);
            Assert.False(missingRisk.Passed);
            Assert.Equal(1, missingRisk.Strategies.Single().ApprovedDecisionsWithoutRiskApproval);
            await using (var db = new TradingBotDbContext(options))
            {
                db.RiskDecisionRecords.Add(new RiskDecisionRecord
                {
                    TimestampUtc = start.AddMinutes(2),
                    DecisionJson = JsonSerializer.Serialize(new
                    {
                        riskDecision = new { decision = 0, reservationId = Guid.NewGuid(), context = new { signalId } }
                    })
                });
                await db.SaveChangesAsync();
            }
            var approved = await ShadowDecisionAssessor.AssessAsync(path, start, start.AddMinutes(5), limits);
            Assert.True(approved.Passed, string.Join(" ", approved.Reasons.Concat(approved.Strategies.SelectMany(x => x.Reasons))));
            Assert.Equal(5m, approved.Strategies.Single().AverageExpectedNetEdgeBps);
            Assert.Equal(2m, approved.Strategies.Single().ModelHoldoutExpectancyBps);
            Assert.Equal(1m, approved.Strategies.Single().BaselineHoldoutExpectancyBps);
            limits.MaximumPendingPatternQueueDepth = 1;
            var backedUp = await ShadowDecisionAssessor.AssessAsync(path, start, start.AddMinutes(5), limits);
            Assert.False(backedUp.Passed);
            Assert.Equal(2, backedUp.Strategies.Single().PeakPendingPatternQueueDepth);
            limits.MaximumPendingPatternQueueDepth = 192;
            await using (var db = new TradingBotDbContext(options))
            {
                var timing = await db.AiAnalysisRecords.SingleAsync(x => x.Context == "ShadowPatternTiming");
                timing.ResultJson = JsonSerializer.Serialize(new
                {
                    SignalId = Guid.NewGuid(), InstrumentId = "US-STK-SPY-SMART", StrategyId = "a",
                    signalAgeMilliseconds = 200, processingMilliseconds = 50, pendingPatternQueueDepth = 2
                });
                await db.SaveChangesAsync();
            }
            var mistimed = await ShadowDecisionAssessor.AssessAsync(path, start, start.AddMinutes(5), limits);
            Assert.False(mistimed.Passed);
            Assert.Contains(mistimed.Strategies.Single().Reasons, x => x.Contains("lack timing", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task BoundedPatternQueue_BackpressuresConcurrentInstrumentAndStrategyLoadWithoutLosingSignals()
    {
        using var bus = new TradingEventBus(new TradingEventBusOptions { PatternCandidateCapacity = 32 },
            NullLogger<TradingEventBus>.Instance);
        const int perStrategy = 100;
        var sources = new[] { "SPY", "QQQ", "IWM" };
        var strategies = new[] { "alpha", "beta", "gamma" };
        var expected = sources.Length * strategies.Length * perStrategy;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new HashSet<Guid>();
        var consumer = Task.Run(async () =>
        {
            for (var i = 0; i < expected; i++)
            {
                var pattern = await bus.PatternCandidateReader.ReadAsync(timeout.Token);
                Assert.True(received.Add(pattern.Context.SignalId));
            }
        });
        var producers = (from symbol in sources from strategy in strategies select Task.Run(async () =>
        {
            for (var i = 0; i < perStrategy; i++)
            {
                var context = PipelineContext.CreateForSignal($"US-STK-{symbol}-SMART", $"{strategy}:{i}", strategy);
                var pattern = new PatternCandidate(PatternType.Hammer, symbol, Timeframe.FiveSeconds,
                    new DateTime(2026, 10, 7, 15, 0, 0, DateTimeKind.Utc).AddSeconds(i), 0.8m, context: context);
                Assert.True(await bus.PublishAsync(pattern, timeout.Token));
            }
        })).ToArray();
        await Task.WhenAll(producers.Append(consumer));
        Assert.Equal(expected, received.Count);
        Assert.Equal(0, bus.PatternCandidateReader.Count);
    }

    private static CanonicalMarketDataEvent Tick(MarketDataEventKind kind, DateTime eventTime, int lagMs) => new()
    {
        InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", Kind = kind,
        EventTimeUtc = eventTime, ReceivedTimeUtc = eventTime.AddMilliseconds(lagMs)
    };
}
