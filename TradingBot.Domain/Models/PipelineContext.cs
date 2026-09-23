using System.Security.Cryptography;
using System.Text;

namespace TradingBot.Domain.Models
{
    public static class PipelineContractVersions
    {
        public const string MarketData = "market-data-v1";
        public const string Features = "features-v1";
        public const string Patterns = "patterns-v1";
        public const string Strategy = "strategy-v1";
        public const string DefaultStrategyId = "deterministic-patterns";
    }

    public sealed record PipelineContext
    {
        public Guid CorrelationId { get; }
        public Guid SignalId { get; }
        public string InstrumentId { get; }
        public string StrategyId { get; }
        public string MarketDataVersion { get; }
        public string FeatureVersion { get; }
        public string PatternVersion { get; }
        public string StrategyVersion { get; }

        public PipelineContext(
            Guid correlationId,
            Guid signalId,
            string instrumentId,
            string strategyId,
            string marketDataVersion,
            string featureVersion,
            string patternVersion,
            string strategyVersion)
        {
            if (correlationId == Guid.Empty) throw new ArgumentException("correlation ID required", nameof(correlationId));
            if (signalId == Guid.Empty) throw new ArgumentException("signal ID required", nameof(signalId));
            if (string.IsNullOrWhiteSpace(instrumentId)) throw new ArgumentException("instrument ID required", nameof(instrumentId));
            if (string.IsNullOrWhiteSpace(strategyId)) throw new ArgumentException("strategy ID required", nameof(strategyId));
            if (string.IsNullOrWhiteSpace(marketDataVersion)) throw new ArgumentException("market data version required", nameof(marketDataVersion));
            if (string.IsNullOrWhiteSpace(featureVersion)) throw new ArgumentException("feature version required", nameof(featureVersion));
            if (string.IsNullOrWhiteSpace(patternVersion)) throw new ArgumentException("pattern version required", nameof(patternVersion));
            if (string.IsNullOrWhiteSpace(strategyVersion)) throw new ArgumentException("strategy version required", nameof(strategyVersion));

            CorrelationId = correlationId;
            SignalId = signalId;
            InstrumentId = instrumentId.Trim();
            StrategyId = strategyId.Trim();
            MarketDataVersion = marketDataVersion.Trim();
            FeatureVersion = featureVersion.Trim();
            PatternVersion = patternVersion.Trim();
            StrategyVersion = strategyVersion.Trim();
        }

        public static PipelineContext Create(string instrumentId, string strategyId = PipelineContractVersions.DefaultStrategyId)
        {
            return new PipelineContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                instrumentId,
                strategyId,
                PipelineContractVersions.MarketData,
                PipelineContractVersions.Features,
                PipelineContractVersions.Patterns,
                PipelineContractVersions.Strategy);
        }

        public static PipelineContext CreateForSignal(
            string instrumentId,
            string signalKey,
            string strategyId = PipelineContractVersions.DefaultStrategyId)
        {
            if (string.IsNullOrWhiteSpace(signalKey)) throw new ArgumentException("signal key required", nameof(signalKey));

            var canonicalKey = $"{instrumentId.Trim().ToUpperInvariant()}|{strategyId.Trim().ToUpperInvariant()}|{signalKey.Trim().ToUpperInvariant()}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey));
            var signalIdBytes = hash.AsSpan(0, 16).ToArray();

            return new PipelineContext(
                Guid.NewGuid(),
                new Guid(signalIdBytes),
                instrumentId,
                strategyId,
                PipelineContractVersions.MarketData,
                PipelineContractVersions.Features,
                PipelineContractVersions.Patterns,
                PipelineContractVersions.Strategy);
        }
    }
}
