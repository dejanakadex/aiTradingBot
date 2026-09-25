using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class PatternDetectionInput
    {
        public IReadOnlyList<Candle> Candles { get; init; } = Array.Empty<Candle>();
        public string StrategyId { get; init; } = PipelineContractVersions.DefaultStrategyId;
        public string FeatureVersion { get; init; } = PipelineContractVersions.Features;
        public IReadOnlyCollection<TradeDirection> Directions { get; init; } = new[] { TradeDirection.Long };
    }
}
