using TradingBot.Application.DTOs;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IPortfolioRiskService
    {
        Task<PortfolioReservationResult> TryReserveAsync(
            StrategyDecision strategyDecision,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions,
            IReadOnlyList<OrderStatusDto> openOrders,
            PositionSizingResult proposedSizing,
            CancellationToken cancellationToken = default);

        Task CommitAsync(Guid reservationId, string? brokerOrderId, CancellationToken cancellationToken = default);
        Task ReleaseAsync(Guid reservationId, string reason, CancellationToken cancellationToken = default);
        Task<PortfolioRiskStateDto> GetStateAsync(string? accountId = null, CancellationToken cancellationToken = default);
    }
}
