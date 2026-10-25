using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

public sealed partial class PortfolioRiskService
{
    public async Task<(RiskDecision Risk, ForexRiskAssessment Assessment)> ReserveForexAsync(
        StrategyDecision decision, string accountId, ForexRiskService calculator,
        Func<IReadOnlyList<PortfolioRiskReservationRecord>, CancellationToken, Task<ForexRiskRequest>> refresh,
        CancellationToken cancellationToken = default)
    {
        var context = decision.Context ?? throw new InvalidOperationException("FX reservation needs pipeline context.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var now = _clock.UtcNow;
            await ExpireAsync(db, now, cancellationToken);
            var key = $"{accountId.Trim().ToUpperInvariant()}|{context.SignalId:N}";
            if (await db.PortfolioRiskReservationRecords.AnyAsync(x => x.ReservationKey == key, cancellationToken))
                throw new InvalidOperationException("FX signal already has a reservation; reconcile rather than resubmit.");
            var active = await db.PortfolioRiskReservationRecords.Where(x => x.AccountId == accountId
                && (x.Status == PortfolioReservationStatus.Pending || x.Status == PortfolioReservationStatus.Committed)).ToArrayAsync(cancellationToken);
            if (active.Length >= _settings.MaximumPendingReservations
                || active.Count(x => x.InstrumentId == context.InstrumentId) >= _settings.MaximumPendingReservationsPerInstrument
                || active.Count(x => x.StrategyId == context.StrategyId) >= _settings.MaximumPendingReservationsPerStrategy)
                throw new InvalidOperationException("FX pending reservation limit reached.");
            if (_settings.InstrumentCooldownSeconds > 0 && await db.PortfolioRiskReservationRecords.AnyAsync(x => x.AccountId == accountId
                && x.InstrumentId == context.InstrumentId && x.CreatedAtUtc > now.AddSeconds(-_settings.InstrumentCooldownSeconds), cancellationToken))
                throw new InvalidOperationException("Instrument cooldown is active.");
            var request = await refresh(active, cancellationToken).ConfigureAwait(false);
            if (!decision.Approved || request.InstrumentId != context.InstrumentId || request.StrategyId != context.StrategyId
                || request.Direction != TradeDirection.Long || request.AsOfUtc > _clock.UtcNow)
                throw new InvalidOperationException("FX reservation request does not match the approved strategy.");
            var assessment = calculator.Evaluate(request);
            if (!assessment.Passed) throw new InvalidOperationException(string.Join(" ", assessment.Reasons));
            now = _clock.UtcNow;
            if (now - request.AsOfUtc > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("FX risk snapshot expired during reservation.");
            var id = Guid.NewGuid();
            db.PortfolioRiskReservationRecords.Add(new()
            {
                Id = id, ReservationKey = key, AccountId = accountId, CorrelationId = context.CorrelationId, SignalId = context.SignalId,
                InstrumentId = context.InstrumentId, Symbol = decision.Symbol, StrategyId = context.StrategyId,
                Quantity = assessment.BaseUnits, ReferencePrice = assessment.EntryPrice, PositionValue = assessment.NotionalUsd,
                RiskAmount = assessment.StopRiskUsd, SignedExposure = assessment.NotionalUsd, Status = PortfolioReservationStatus.Pending,
                CreatedAtUtc = now, UpdatedAtUtc = now, ExpiresAtUtc = now.AddSeconds(_settings.ReservationTimeoutSeconds),
                Reason = "FX USD risk and base-unit capacity reserved.", Version = 1
            });
            db.ForexExecutionProofRecords.Add(new() { ReservationId = id, InstrumentId = context.InstrumentId, CreatedUtc = now,
                RequestJson = JsonSerializer.Serialize(request), AssessmentJson = JsonSerializer.Serialize(assessment) });
            AddAudit(db, id, null, PortfolioReservationStatus.Pending, "FX capacity reserved with durable assessment.", now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (new RiskDecision(RiskDecisionType.Approve, "FX risk passed.", assessment.BaseUnits, assessment.NotionalUsd,
                assessment.StopRiskUsd, now, [], context, id), assessment);
        }
        finally { _gate.Release(); }
    }
}
