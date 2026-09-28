using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record PortfolioReservationResult(
        bool Approved,
        Guid? ReservationId,
        decimal Quantity,
        decimal PositionValue,
        decimal RiskAmount,
        IReadOnlyList<string> RejectionReasons);

    public sealed record PortfolioRiskReservationDto(
        Guid Id,
        string AccountId,
        Guid SignalId,
        string InstrumentId,
        string Symbol,
        string StrategyId,
        decimal Quantity,
        decimal PositionValue,
        decimal RiskAmount,
        decimal SignedExposure,
        PortfolioReservationStatus Status,
        DateTime CreatedAtUtc,
        DateTime ExpiresAtUtc,
        string? BrokerOrderId,
        string Reason);

    public sealed record PortfolioRiskStateDto(
        string? AccountId,
        decimal PositionGrossExposure,
        decimal PositionNetExposure,
        decimal ReservedGrossExposure,
        decimal ReservedNetExposure,
        IReadOnlyList<PortfolioRiskReservationDto> ActiveReservations);
}
