using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ITradePipelineChannel
    {
        ChannelReader<ApprovedTradePlan> ApprovedTradePlanReader { get; }

        ValueTask PublishAsync(ApprovedTradePlan plan, CancellationToken cancellationToken = default);
    }
}
