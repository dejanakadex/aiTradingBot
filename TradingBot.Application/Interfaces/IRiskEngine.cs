using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IRiskEngine
    {
        Task<RiskDecision> EvaluateAsync(
            StrategyDecision strategyDecision,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions,
            IReadOnlyList<Trade> todaysCompletedTrades,
            IReadOnlyList<OrderStatusDto> openOrders,
            CancellationToken cancellationToken = default);
    }
}
