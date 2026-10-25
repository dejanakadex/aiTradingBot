using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

/// <summary>Explicit opt-in research query/persistence; no event bus, strategy approval or order dependency.</summary>
public sealed class ForexPatternResearchService(ForexFeatureService inputs, IFeatureEngine features,
    ICandidateResearchService research, IOptions<ForexPatternSettings> settings, IOptions<TradingSettings> trading,
    IOptions<ShortAggregateSettings> aggregates, ILogger<PatternDetector> logger)
{
    public async Task<PatternDetectionBatch> EvaluateAndPersistAsync(string instrumentId, string strategyId, int intervalSeconds,
        DateTime asOfUtc, string sourceEventId, Guid? replayRunId = null, CancellationToken cancellationToken = default)
    {
        var errors = settings.Value.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        var instrument = trading.Value.GetConfiguredInstruments().Single(x => x.InstrumentId == instrumentId);
        if (instrument.InstrumentType != InstrumentType.Forex || !instrument.StrategyIds.Contains(strategyId)
            || !aggregates.Value.IntervalsSeconds.Contains(intervalSeconds))
            throw new ArgumentException("FX research requires a configured FX instrument, strategy and aggregate interval.");
        var profile = settings.Value.Profiles.Single(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId && x.IntervalSeconds == intervalSeconds);
        var input = inputs.GetInput(instrumentId, intervalSeconds, asOfUtc);
        if (input == null) return new PatternDetectionBatch();
        var batch = new ForexPatternDetector(profile, features, logger).Evaluate(input);
        await research.PersistBatchAsync(batch, sourceEventId, replayRunId,
            new CandidateEvaluationContext(MarketRegime.Unknown, null, null, asOfUtc, input.Bid, input.BidTimeUtc, input.Ask, input.AskTimeUtc), cancellationToken);
        return batch;
    }
}
