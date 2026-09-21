using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class TradingPipelineActivitySnapshot
    {
        public TradingPipelineStage Stage { get; init; } = TradingPipelineStage.WaitingForCandle;
        public TradingPipelineActivityState State { get; init; } = TradingPipelineActivityState.Waiting;
        public string Title { get; init; } = "Waiting";
        public string Status { get; init; } = "Waiting for next candle.";
        public string Symbol { get; init; } = string.Empty;
        public string Pattern { get; init; } = string.Empty;
        public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    }
}
