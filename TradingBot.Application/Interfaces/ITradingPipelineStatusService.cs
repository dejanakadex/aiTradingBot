using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ITradingPipelineStatusService
    {
        TradingPipelineActivitySnapshot Current { get; }

        void MarkWaiting(string status = "Waiting for next candle.");

        void Mark(
            TradingPipelineStage stage,
            TradingPipelineActivityState state,
            string status,
            string? symbol = null,
            string? pattern = null);
    }
}
