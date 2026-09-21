using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class TradePipelineChannel : ITradePipelineChannel
    {
        private readonly ILogger<TradePipelineChannel> _logger;
        private readonly Channel<ApprovedTradePlan> _approvedTradePlans = Channel.CreateBounded<ApprovedTradePlan>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

        public TradePipelineChannel(ILogger<TradePipelineChannel>? logger = null)
        {
            _logger = logger ?? NullLogger<TradePipelineChannel>.Instance;
        }

        public ChannelReader<ApprovedTradePlan> ApprovedTradePlanReader => _approvedTradePlans.Reader;

        public async ValueTask PublishAsync(ApprovedTradePlan plan, CancellationToken cancellationToken = default)
        {
            if (_approvedTradePlans.Writer.TryWrite(plan))
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(250));

            try
            {
                if (await _approvedTradePlans.Writer.WaitToWriteAsync(timeout.Token).ConfigureAwait(false)
                    && _approvedTradePlans.Writer.TryWrite(plan))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall through to the fail-closed drop/log path below.
            }

            _logger.LogWarning(
                "Approved trade plan channel is full; dropping approved plan for {Symbol}. This fails closed because no broker order will be submitted.",
                plan.StrategyDecision.Symbol);
        }
    }
}
