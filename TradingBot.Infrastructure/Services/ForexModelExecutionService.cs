using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

public sealed class ForexModelExecutionService(IOptions<ForexResearchSettings> settings, IOptions<TradingSettings> trading,
    ForexModelDecisionService decisions, ForexPaperPreparationService preparation, ITradePipelineChannel channel,
    PortfolioRiskService portfolio, ISignalArbitrationService arbitration)
{
    public async Task ProcessAsync(PatternCandidate candidate, CancellationToken ct)
    {
        if (!settings.Value.AutomaticPaperSignalsEnabled || !trading.Value.Enabled || !trading.Value.ForexPaperExecutionEnabled
            || !trading.Value.GetConfiguredInstruments().Single(x => x.InstrumentId == candidate.InstrumentId).TradingEnabled) return;
        var proposal = await decisions.CreateProposalAsync(candidate, ct);
        var prepared = await preparation.PrepareAsync(proposal, ct);
        try
        {
            if (!await channel.PublishAsync(prepared, ct)) throw new InvalidOperationException("FX execution queue refused the prepared plan.");
        }
        catch
        {
            if (prepared.RiskDecision.ReservationId is Guid reservation) await portfolio.ReleaseAsync(reservation, "FX model plan was not queued.", CancellationToken.None);
            if (prepared.ArbitrationId is Guid intent) await arbitration.RejectAsync(intent, "FX model plan was not queued.", CancellationToken.None);
            throw;
        }
    }
}
