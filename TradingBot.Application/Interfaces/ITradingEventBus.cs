using System.Threading;
using System.Threading.Channels;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ITradingEventBus
    {
        ChannelReader<Candle> CandleReader { get; }
        ChannelReader<PatternCandidate> PatternCandidateReader { get; }
        ChannelReader<AiMarketAnalysis> AiMarketAnalysisReader { get; }
        ChannelReader<TradeSignal> TradeSignalReader { get; }
        ChannelReader<OrderRequest> OrderRequestReader { get; }

        bool TryPublish(Candle candle);
        System.Threading.Tasks.ValueTask<bool> PublishAsync(Candle candle, CancellationToken cancellationToken = default);

        bool TryPublish(PatternCandidate pattern);
        System.Threading.Tasks.ValueTask<bool> PublishAsync(PatternCandidate pattern, CancellationToken cancellationToken = default);

        bool TryPublish(AiMarketAnalysis analysis);
        System.Threading.Tasks.ValueTask<bool> PublishAsync(AiMarketAnalysis analysis, CancellationToken cancellationToken = default);

        bool TryPublish(TradeSignal signal);
        System.Threading.Tasks.ValueTask<bool> PublishAsync(TradeSignal signal, CancellationToken cancellationToken = default);

        bool TryPublish(OrderRequest order);
        System.Threading.Tasks.ValueTask<bool> PublishAsync(OrderRequest order, CancellationToken cancellationToken = default);
    }
}
