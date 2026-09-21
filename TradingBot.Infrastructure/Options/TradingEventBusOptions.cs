using System;

namespace TradingBot.Infrastructure.Options
{
    public sealed class TradingEventBusOptions
    {
        public int CandleCapacity { get; set; } = 1024;
        public int PatternCandidateCapacity { get; set; } = 256;
        public int AiMarketAnalysisCapacity { get; set; } = 256;
        public int TradeSignalCapacity { get; set; } = 512;
        public int OrderRequestCapacity { get; set; } = 512;

        public ChannelFullMode FullMode { get; set; } = ChannelFullMode.DropOldest;
    }
}
