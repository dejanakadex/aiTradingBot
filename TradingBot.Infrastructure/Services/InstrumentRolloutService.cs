using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public sealed class InstrumentRolloutService : IInstrumentRolloutService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IInstrumentRegistryService _registry;
        private readonly ITradingEngineStatusService _engineStatus;
        private readonly InstrumentRolloutSettings _settings;
        private readonly TradingSettings _tradingSettings;
        private readonly IClock _clock;

        public InstrumentRolloutService(
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IInstrumentRegistryService registry,
            ITradingEngineStatusService engineStatus,
            IOptions<InstrumentRolloutSettings> settings,
            IOptions<TradingSettings> tradingSettings,
            IClock clock)
        {
            _dbFactory = dbFactory;
            _registry = registry;
            _engineStatus = engineStatus;
            _settings = settings.Value;
            _tradingSettings = tradingSettings.Value;
            _clock = clock;
        }

        public async Task<IReadOnlyList<InstrumentRolloutEvaluation>> EvaluateAllAsync(bool applyTransitions, CancellationToken cancellationToken = default)
        {
            var instruments = await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<InstrumentRolloutEvaluation>();
            foreach (var instrument in instruments.Where(x => x.ConfiguredEnabled))
            {
                results.Add(await EvaluateAsync(instrument.InstrumentId, applyTransitions, cancellationToken).ConfigureAwait(false));
            }
            return results;
        }

        public async Task<InstrumentRolloutEvaluation> EvaluateAsync(string instrumentId, bool applyTransitions, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(instrumentId)) throw new ArgumentException("Instrument ID is required.", nameof(instrumentId));
            var instrument = await _registry.GetAsync(instrumentId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Instrument '{instrumentId}' is not registered.");
            var now = ToUtc(_clock.UtcNow);
            var windowStart = now.AddHours(-_settings.LookbackHours);

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var decisions = await db.ScalpingExecutionDecisionRecords.AsNoTracking()
                .Where(x => x.InstrumentId == instrument.InstrumentId && x.EvaluatedAtUtc >= windowStart)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var streams = await db.MarketDataStreamStateRecords.AsNoTracking()
                .Where(x => x.InstrumentId == instrument.InstrumentId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var qualityIncidents = await db.MarketDataQualityIncidentRecords.AsNoTracking()
                .CountAsync(x => x.InstrumentId == instrument.InstrumentId && x.RecordedAtUtc >= windowStart, cancellationToken)
                .ConfigureAwait(false);
            var arbitrations = await db.SignalArbitrationRecords.AsNoTracking()
                .Where(x => x.InstrumentId == instrument.InstrumentId && x.EntryBrokerOrderId != null && x.UpdatedAtUtc >= windowStart)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var brokerIds = arbitrations.Select(x => x.EntryBrokerOrderId!).Distinct().ToArray();
            var orders = brokerIds.Length == 0
                ? new List<OrderRecord>()
                : await db.OrderRecords.AsNoTracking().Where(x => brokerIds.Contains(x.BrokerOrderId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var orderIds = orders.Select(x => x.Id).ToArray();
            var executions = orderIds.Length == 0
                ? new List<ExecutionRecord>()
                : await db.ExecutionRecords.AsNoTracking().Where(x => orderIds.Contains(x.OrderRecordId)).ToListAsync(cancellationToken).ConfigureAwait(false);

            var reasons = new List<string>();
            var critical = new List<string>();
            var unhealthyStreams = streams.Count(x => !x.IsHealthy);
            var unknownOrders = orders.Count(x => x.Status is OrderStatus.Unknown or OrderStatus.PendingBrokerConfirmation or OrderStatus.CancelPending);
            if (streams.Count == 0) critical.Add("No persisted market-data stream exists for the instrument.");
            if (unhealthyStreams > 0) critical.Add($"{unhealthyStreams} market-data stream(s) are unhealthy.");
            if (qualityIncidents > _settings.MaximumRecentQualityIncidents) critical.Add($"Recent market-data incidents {qualityIncidents} exceed {_settings.MaximumRecentQualityIncidents}.");
            if (unknownOrders > 0) critical.Add($"{unknownOrders} broker order(s) have unresolved lifecycle state.");
            var engine = _engineStatus.Current;
            if ((instrument.Status is InstrumentOnboardingStatus.PaperReady or InstrumentOnboardingStatus.LiveEnabled)
                && (!engine.ReconciliationCompleted || engine.Mismatches.Count > 0 || engine.State is TradingEngineState.Degraded or TradingEngineState.Faulted))
                critical.Add("Broker reconciliation is not healthy.");

            var approvedShadow = decisions.Count(x => x.Approved);
            var shadowPassed = decisions.Count >= _settings.MinimumShadowDecisions
                && approvedShadow >= _settings.MinimumApprovedShadowDecisions
                && critical.Count == 0;
            if (decisions.Count < _settings.MinimumShadowDecisions) reasons.Add($"Shadow decisions {decisions.Count}/{_settings.MinimumShadowDecisions}.");
            if (approvedShadow < _settings.MinimumApprovedShadowDecisions) reasons.Add($"Approved shadow decisions {approvedShadow}/{_settings.MinimumApprovedShadowDecisions}.");

            var fillsByOrder = executions.GroupBy(x => x.OrderRecordId).ToDictionary(x => x.Key, x => x.ToArray());
            var filledOrders = orders.Count(x => fillsByOrder.TryGetValue(x.Id, out var fills) && fills.Sum(y => y.Quantity) > 0m);
            var unfilledRatio = orders.Count == 0 ? 0m : (orders.Count - filledOrders) / (decimal)orders.Count;
            var slippages = new List<decimal>();
            var latencies = new List<double>();
            var decisionBySignal = decisions.GroupBy(x => x.SignalId).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.EvaluatedAtUtc).First());
            foreach (var arbitration in arbitrations)
            {
                var order = orders.FirstOrDefault(x => x.BrokerOrderId == arbitration.EntryBrokerOrderId);
                if (order == null || !fillsByOrder.TryGetValue(order.Id, out var fills) || fills.Length == 0) continue;
                var quantity = fills.Sum(x => x.Quantity);
                if (quantity <= 0m) continue;
                var averageFill = fills.Sum(x => x.Price * x.Quantity) / quantity;
                if (decisionBySignal.TryGetValue(arbitration.SignalId, out var decision) && decision.Ask > 0m)
                {
                    slippages.Add((averageFill - decision.Ask) / decision.Ask * 10_000m);
                    latencies.Add(Math.Max(0d, (fills.Min(x => x.TimestampUtc) - decision.EvaluatedAtUtc).TotalMilliseconds));
                }
            }
            var averageSlippage = slippages.Count == 0 ? null : slippages.Average();
            var averageLatency = latencies.Count == 0 ? null : latencies.Average();
            var paperPassed = orders.Count >= _settings.MinimumPaperOrders
                && unfilledRatio <= _settings.MaximumPaperUnfilledRatio
                && averageSlippage.HasValue && averageSlippage.Value <= _settings.MaximumAverageEntrySlippageBps
                && averageLatency.HasValue && averageLatency.Value <= _settings.MaximumAverageFillLatencyMilliseconds
                && critical.Count == 0;
            if (orders.Count < _settings.MinimumPaperOrders) reasons.Add($"Paper orders {orders.Count}/{_settings.MinimumPaperOrders}.");
            if (orders.Count > 0 && unfilledRatio > _settings.MaximumPaperUnfilledRatio) reasons.Add($"Paper unfilled ratio {unfilledRatio:P2} exceeds {_settings.MaximumPaperUnfilledRatio:P2}.");
            if (!averageSlippage.HasValue) reasons.Add("Paper entry slippage is not yet measurable.");
            else if (averageSlippage.Value > _settings.MaximumAverageEntrySlippageBps) reasons.Add($"Average entry slippage {averageSlippage.Value:F4} bps exceeds {_settings.MaximumAverageEntrySlippageBps:F4} bps.");
            if (!averageLatency.HasValue) reasons.Add("Paper fill latency is not yet measurable.");
            else if (averageLatency.Value > _settings.MaximumAverageFillLatencyMilliseconds) reasons.Add($"Average fill latency {averageLatency.Value:F0} ms exceeds {_settings.MaximumAverageFillLatencyMilliseconds} ms.");
            reasons.AddRange(critical);

            var statusAfter = instrument.Status;
            var suspended = false;
            if (_settings.Enabled && applyTransitions)
            {
                if (_settings.AutoSuspend && critical.Count > 0 && (instrument.Status is InstrumentOnboardingStatus.PaperReady or InstrumentOnboardingStatus.LiveEnabled))
                {
                    statusAfter = (await _registry.TransitionAsync(instrument.InstrumentId, instrument.Status, InstrumentOnboardingStatus.Suspended,
                        string.Join(" ", critical), "RolloutMonitor", cancellationToken).ConfigureAwait(false)).Status;
                    suspended = true;
                }
                else if (_settings.AutoAdvanceToShadow && instrument.Status == InstrumentOnboardingStatus.ResearchReady && critical.Count == 0)
                {
                    statusAfter = (await _registry.TransitionAsync(instrument.InstrumentId, instrument.Status, InstrumentOnboardingStatus.ShadowReady,
                        "Market-data health criteria passed; shadow observation started.", "RolloutMonitor", cancellationToken).ConfigureAwait(false)).Status;
                }
                else if (_settings.AutoAdvanceToPaper && instrument.Status == InstrumentOnboardingStatus.ShadowReady && shadowPassed)
                {
                    statusAfter = (await _registry.TransitionAsync(instrument.InstrumentId, instrument.Status, InstrumentOnboardingStatus.PaperReady,
                        "Shadow sample and data-health criteria passed.", "RolloutMonitor", cancellationToken).ConfigureAwait(false)).Status;
                }
            }

            var record = new InstrumentRolloutEvaluationRecord
            {
                InstrumentId = instrument.InstrumentId,
                Symbol = instrument.Symbol,
                StatusBefore = instrument.Status,
                StatusAfter = statusAfter,
                EvaluatedAtUtc = now,
                WindowStartUtc = windowStart,
                ShadowDecisionCount = decisions.Count,
                ApprovedShadowDecisionCount = approvedShadow,
                PaperOrderCount = orders.Count,
                FilledPaperOrderCount = filledOrders,
                PaperUnfilledRatio = unfilledRatio,
                AverageEntrySlippageBps = averageSlippage,
                AverageFillLatencyMilliseconds = averageLatency,
                QualityIncidentCount = qualityIncidents,
                UnhealthyStreamCount = unhealthyStreams,
                UnknownOrderCount = unknownOrders,
                ShadowCriteriaPassed = shadowPassed,
                PaperCriteriaPassed = paperPassed,
                EligibleForManualLiveApproval = instrument.Status == InstrumentOnboardingStatus.PaperReady && paperPassed,
                Suspended = suspended,
                ReasonsJson = JsonSerializer.Serialize(reasons, JsonOptions)
            };
            db.InstrumentRolloutEvaluationRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToDto(record);
        }

        public async Task<IReadOnlyList<InstrumentRolloutEvaluation>> GetRecentAsync(string? instrumentId = null, int count = 100, CancellationToken cancellationToken = default)
        {
            count = Math.Clamp(count, 1, 1000);
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var query = db.InstrumentRolloutEvaluationRecords.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(instrumentId)) query = query.Where(x => x.InstrumentId == instrumentId.Trim());
            var records = await query.OrderByDescending(x => x.EvaluatedAtUtc).ThenByDescending(x => x.Id).Take(count).ToListAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToDto).ToArray();
        }

        public async Task<InstrumentRegistrySnapshot> ApproveLiveAsync(string instrumentId, ManualLiveApprovalRequest request, CancellationToken cancellationToken = default)
        {
            if (!request.ConfirmLiveTrading) throw new InvalidOperationException("Explicit live confirmation is required.");
            if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("Live approval reason is required.", nameof(request));
            if (!_tradingSettings.LiveTradingExplicitlyEnabled) throw new InvalidOperationException("LiveTradingExplicitlyEnabled must be true before manual live approval.");
            var instrument = await _registry.GetAsync(instrumentId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Instrument '{instrumentId}' is not registered.");
            if (instrument.Version != request.ExpectedVersion) throw new InvalidOperationException($"Instrument version is {instrument.Version}, expected {request.ExpectedVersion}.");
            if (instrument.Status != InstrumentOnboardingStatus.PaperReady) throw new InvalidOperationException("Only a PaperReady instrument can receive manual live approval.");
            var evaluation = await EvaluateAsync(instrumentId, false, cancellationToken).ConfigureAwait(false);
            if (!evaluation.PaperCriteriaPassed) throw new InvalidOperationException($"Paper rollout criteria have not passed: {string.Join(" ", evaluation.Reasons)}");
            return await _registry.TransitionAsync(instrumentId, InstrumentOnboardingStatus.PaperReady, InstrumentOnboardingStatus.LiveEnabled,
                request.Reason.Trim(), "ManualLiveApproval", cancellationToken).ConfigureAwait(false);
        }

        private static InstrumentRolloutEvaluation ToDto(InstrumentRolloutEvaluationRecord record) => new()
        {
            Id = record.Id, InstrumentId = record.InstrumentId, Symbol = record.Symbol, StatusBefore = record.StatusBefore,
            StatusAfter = record.StatusAfter, EvaluatedAtUtc = record.EvaluatedAtUtc, WindowStartUtc = record.WindowStartUtc,
            ShadowDecisionCount = record.ShadowDecisionCount, ApprovedShadowDecisionCount = record.ApprovedShadowDecisionCount,
            PaperOrderCount = record.PaperOrderCount, FilledPaperOrderCount = record.FilledPaperOrderCount,
            PaperUnfilledRatio = record.PaperUnfilledRatio, AverageEntrySlippageBps = record.AverageEntrySlippageBps,
            AverageFillLatencyMilliseconds = record.AverageFillLatencyMilliseconds, QualityIncidentCount = record.QualityIncidentCount,
            UnhealthyStreamCount = record.UnhealthyStreamCount, UnknownOrderCount = record.UnknownOrderCount,
            ShadowCriteriaPassed = record.ShadowCriteriaPassed, PaperCriteriaPassed = record.PaperCriteriaPassed,
            EligibleForManualLiveApproval = record.EligibleForManualLiveApproval, Suspended = record.Suspended,
            Reasons = JsonSerializer.Deserialize<string[]>(record.ReasonsJson, JsonOptions) ?? Array.Empty<string>()
        };

        private static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}
