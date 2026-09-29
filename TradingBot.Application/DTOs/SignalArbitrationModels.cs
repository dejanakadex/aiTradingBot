using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record SignalArbitrationResult(bool Approved, Guid? ArbitrationId, string Reason);

    public sealed record SignalExecutionClaimResult(bool Approved, string Reason);

    public sealed record VirtualAllocationDto(
        Guid Id,
        string AccountId,
        Guid SignalId,
        string InstrumentId,
        string Symbol,
        string StrategyId,
        TradeDirection Direction,
        int Priority,
        SignalArbitrationStatus Status,
        Guid? ReservationId,
        decimal ApprovedQuantity,
        decimal PositionValue,
        decimal FilledQuantity,
        decimal ExitedQuantity,
        decimal OpenQuantity,
        string? EntryBrokerOrderId,
        IReadOnlyList<string> ExitBrokerOrderIds,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc,
        string Reason);

    public sealed record SignalArbitrationStateDto(
        string? AccountId,
        string? InstrumentId,
        IReadOnlyList<VirtualAllocationDto> Allocations);
}
