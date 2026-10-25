using TradingBot.Application.Configuration;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Infrastructure.Interfaces;

public sealed record ForexBrokerRulesSnapshot(IbkrContractProbe Contract, DateTime ObservedUtc, ForexOrderRules Rules);
public interface IForexBrokerRules
{
    Task<ForexBrokerRulesSnapshot> GetForexOrderRulesAsync(ConfiguredInstrument instrument, CancellationToken cancellationToken = default);
}
