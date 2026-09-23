using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;

namespace TradingBot.Application.Interfaces
{
    public interface IInstrumentRegistryService
    {
        Task<InstrumentRegistrySyncResult> SynchronizeConfiguredAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<InstrumentRegistrySnapshot>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<InstrumentRegistrySnapshot?> GetAsync(string instrumentId, CancellationToken cancellationToken = default);
        Task<InstrumentRegistrySnapshot> SetBrokerContractAsync(
            string instrumentId,
            int expectedVersion,
            long brokerContractId,
            string brokerPrimaryExchange,
            CancellationToken cancellationToken = default);
        Task<InstrumentRegistrySnapshot> TransitionAsync(
            string instrumentId,
            InstrumentOnboardingStatus expectedStatus,
            InstrumentOnboardingStatus targetStatus,
            string reason,
            string trigger,
            CancellationToken cancellationToken = default);
    }
}
