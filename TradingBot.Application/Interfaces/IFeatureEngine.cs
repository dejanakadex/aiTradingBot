using System.Collections.Generic;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IFeatureEngine
    {
        string FeatureVersion => PipelineContractVersions.Features;

        /// <summary>
        /// Compute market features for the most recent candle given a sequence of historical candles (ascending by time).
        /// Returns a MarketFeatures instance where unavailable indicators are null.
        /// </summary>
        MarketFeatures ComputeFeatures(IReadOnlyList<Candle> candles);

        MarketFeatures ComputeFeatures(CanonicalFeatureInput input) => ComputeFeatures(input.Candles);
    }
}
