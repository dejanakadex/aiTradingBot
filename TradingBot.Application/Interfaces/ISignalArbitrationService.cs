using TradingBot.Application.DTOs;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ISignalArbitrationService
    {
        Task<SignalArbitrationResult> ArbitrateAsync(PatternCandidate pattern, string accountId, IReadOnlyList<PositionDto> currentPositions, CancellationToken cancellationToken = default);
        Task BindReservationAsync(Guid arbitrationId, Guid reservationId, decimal approvedQuantity, decimal positionValue, CancellationToken cancellationToken = default);
        Task<SignalExecutionClaimResult> ClaimForExecutionAsync(Guid arbitrationId, CancellationToken cancellationToken = default);
        Task RecordSubmissionAsync(Guid arbitrationId, ManagedOrderResult result, CancellationToken cancellationToken = default);
        Task RecordEntryFillAsync(Guid signalId, decimal cumulativeFilledQuantity, CancellationToken cancellationToken = default);
        Task RegisterExitOrderAsync(Guid signalId, string brokerOrderId, decimal quantity, CancellationToken cancellationToken = default);
        Task<SignalExecutionClaimResult> ValidateExitQuantityAsync(Guid signalId, decimal quantity, CancellationToken cancellationToken = default);
        Task RejectAsync(Guid arbitrationId, string reason, CancellationToken cancellationToken = default);
        Task CompleteAnalysisOnlyAsync(Guid arbitrationId, CancellationToken cancellationToken = default);
        Task<SignalArbitrationStateDto> GetStateAsync(string? accountId = null, string? instrumentId = null, CancellationToken cancellationToken = default);
    }
}
