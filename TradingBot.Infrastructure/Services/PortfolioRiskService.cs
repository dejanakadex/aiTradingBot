using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class PortfolioRiskService : IPortfolioRiskService
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly RiskSettings _settings;
        private readonly IClock _clock;
        private readonly ILogger<PortfolioRiskService> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public PortfolioRiskService(
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IOptions<RiskSettings> settings,
            IClock clock,
            ILogger<PortfolioRiskService> logger)
        {
            _dbFactory = dbFactory;
            _settings = settings.Value;
            _clock = clock;
            _logger = logger;
        }

        public async Task<PortfolioReservationResult> TryReserveAsync(
            StrategyDecision strategyDecision,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions,
            IReadOnlyList<OrderStatusDto> openOrders,
            PositionSizingResult proposedSizing,
            CancellationToken cancellationToken = default)
        {
            var context = strategyDecision.Context;
            if (context == null)
            {
                return Rejected("Pipeline context is required for an atomic portfolio reservation.");
            }

            if (proposedSizing.Quantity <= 0m || proposedSizing.PositionValue <= 0m || strategyDecision.EntryMin is not > 0m)
            {
                return Rejected("Proposed position has zero capacity.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                var now = _clock.UtcNow;
                await ExpireAsync(db, now, cancellationToken).ConfigureAwait(false);

                var accountId = accountInfo.AccountId.Trim();
                var reservationKey = $"{accountId.ToUpperInvariant()}|{context.SignalId:N}";
                var existing = await db.PortfolioRiskReservationRecords
                    .SingleOrDefaultAsync(x => x.ReservationKey == reservationKey, cancellationToken)
                    .ConfigureAwait(false);
                if (existing != null)
                {
                    if (IsActive(existing.Status) && existing.ExpiresAtUtc > now)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return new PortfolioReservationResult(true, existing.Id, existing.Quantity, existing.PositionValue, existing.RiskAmount, Array.Empty<string>());
                    }

                    return Rejected("This signal already has a terminal portfolio reservation.");
                }

                var active = await db.PortfolioRiskReservationRecords
                    .Where(x => x.AccountId == accountId
                        && (x.Status == PortfolioReservationStatus.Pending || x.Status == PortfolioReservationStatus.Committed)
                        && x.ExpiresAtUtc > now)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var reasons = ValidateCounts(strategyDecision, currentPositions, openOrders, active);
                var latestInstrumentReservation = await db.PortfolioRiskReservationRecords
                    .Where(x => x.AccountId == accountId && x.InstrumentId == context.InstrumentId)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Select(x => (DateTime?)x.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (_settings.InstrumentCooldownSeconds > 0
                    && latestInstrumentReservation.HasValue
                    && latestInstrumentReservation.Value.AddSeconds(_settings.InstrumentCooldownSeconds) > now)
                {
                    reasons.Add("Instrument entry cooldown is active.");
                }

                var positions = currentPositions
                    .Where(x => string.IsNullOrWhiteSpace(x.AccountId) || x.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var positionGross = positions.Sum(x => Math.Abs(x.Quantity * x.AveragePrice));
                var positionNet = positions.Sum(x => x.Quantity * x.AveragePrice);
                var reservedGross = active.Sum(x => Math.Abs(x.SignedExposure));
                var reservedNet = active.Sum(x => x.SignedExposure);
                var instrumentExposure = positions
                    .Where(x => x.Symbol.Equals(strategyDecision.Symbol, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => Math.Abs(x.Quantity * x.AveragePrice))
                    + active.Where(x => x.InstrumentId.Equals(context.InstrumentId, StringComparison.OrdinalIgnoreCase)).Sum(x => Math.Abs(x.SignedExposure));
                var strategyExposure = active
                    .Where(x => x.StrategyId.Equals(context.StrategyId, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => Math.Abs(x.SignedExposure));

                var capacities = new List<(string Name, decimal Value)>
                {
                    ("global gross exposure", _settings.MaximumGrossExposure - positionGross - reservedGross),
                    ("global net exposure", _settings.MaximumNetExposure - positionNet - reservedNet),
                    ("instrument exposure", _settings.MaximumInstrumentExposure - instrumentExposure),
                    ("strategy exposure", _settings.MaximumStrategyExposure - strategyExposure),
                    ("buying power", accountInfo.BuyingPower - reservedGross),
                    ("leverage", accountInfo.NetLiquidation * _settings.MaximumLeverage - positionGross - reservedGross)
                };

                var correlationMembers = FindCorrelationMembers(context.InstrumentId, strategyDecision.Symbol);
                if (correlationMembers.Count > 0)
                {
                    var correlatedPositionExposure = positions
                        .Where(x => correlationMembers.Contains(x.Symbol))
                        .Sum(x => Math.Abs(x.Quantity * x.AveragePrice));
                    var correlatedReservationExposure = active
                        .Where(x => correlationMembers.Contains(x.Symbol) || correlationMembers.Contains(x.InstrumentId))
                        .Sum(x => Math.Abs(x.SignedExposure));
                    capacities.Add(("correlation group exposure", _settings.MaximumCorrelationGroupExposure - correlatedPositionExposure - correlatedReservationExposure));
                }

                foreach (var capacity in capacities.Where(x => x.Value <= 0m))
                {
                    reasons.Add($"No {capacity.Name} capacity remains.");
                }

                if (reasons.Count > 0)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new PortfolioReservationResult(false, null, 0m, 0m, 0m, reasons);
                }

                var allowedValue = capacities.Append((Name: "proposed position", Value: proposedSizing.PositionValue)).Min(x => x.Value);
                if (allowedValue <= 0m)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Rejected("No portfolio capacity remains.");
                }

                var scale = allowedValue / proposedSizing.PositionValue;
                var quantity = proposedSizing.Quantity * scale;
                var riskAmount = proposedSizing.RiskAmount * scale;
                if (quantity <= 0m)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Rejected("Portfolio limits reduced the approved quantity to zero.");
                }

                var reservation = new TradingBot.Persistence.PortfolioRiskReservationRecord
                {
                    Id = Guid.NewGuid(),
                    ReservationKey = reservationKey,
                    AccountId = accountId,
                    CorrelationId = context.CorrelationId,
                    SignalId = context.SignalId,
                    InstrumentId = context.InstrumentId,
                    Symbol = strategyDecision.Symbol.Trim().ToUpperInvariant(),
                    StrategyId = context.StrategyId,
                    Quantity = quantity,
                    ReferencePrice = strategyDecision.EntryMin.Value,
                    PositionValue = allowedValue,
                    RiskAmount = riskAmount,
                    SignedExposure = allowedValue,
                    Status = PortfolioReservationStatus.Pending,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    ExpiresAtUtc = now.AddSeconds(_settings.ReservationTimeoutSeconds),
                    Reason = "Reserved after portfolio risk approval.",
                    Version = 1
                };
                db.PortfolioRiskReservationRecords.Add(reservation);
                AddAudit(db, reservation.Id, null, PortfolioReservationStatus.Pending, reservation.Reason, now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Reserved portfolio capacity {ReservationId} for {InstrumentId}/{StrategyId}: value={PositionValue}, expires={ExpiresAtUtc}",
                    reservation.Id,
                    reservation.InstrumentId,
                    reservation.StrategyId,
                    reservation.PositionValue,
                    reservation.ExpiresAtUtc);

                return new PortfolioReservationResult(true, reservation.Id, quantity, allowedValue, riskAmount, Array.Empty<string>());
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task CommitAsync(Guid reservationId, string? brokerOrderId, CancellationToken cancellationToken = default) =>
            TransitionAsync(reservationId, PortfolioReservationStatus.Committed, "Broker accepted the order.", brokerOrderId, cancellationToken);

        public Task ReleaseAsync(Guid reservationId, string reason, CancellationToken cancellationToken = default) =>
            TransitionAsync(reservationId, PortfolioReservationStatus.Released, reason, null, cancellationToken);

        public async Task<PortfolioRiskStateDto> GetStateAsync(string? accountId = null, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await ExpireAsync(db, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var query = db.PortfolioRiskReservationRecords.AsNoTracking()
                    .Where(x => x.Status == PortfolioReservationStatus.Pending || x.Status == PortfolioReservationStatus.Committed);
                if (!string.IsNullOrWhiteSpace(accountId)) query = query.Where(x => x.AccountId == accountId);
                var records = await query.OrderBy(x => x.CreatedAtUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
                var items = records.Select(ToDto).ToArray();
                return new PortfolioRiskStateDto(accountId, 0m, 0m, records.Sum(x => Math.Abs(x.SignedExposure)), records.Sum(x => x.SignedExposure), items);
            }
            finally
            {
                _gate.Release();
            }
        }

        private List<string> ValidateCounts(
            StrategyDecision strategyDecision,
            IReadOnlyList<PositionDto> positions,
            IReadOnlyList<OrderStatusDto> openOrders,
            IReadOnlyList<TradingBot.Persistence.PortfolioRiskReservationRecord> active)
        {
            var context = strategyDecision.Context!;
            var reasons = new List<string>();
            var openPositionCount = positions.Count(x => x.Quantity != 0m);
            var openOrderCount = openOrders.Count(x => IsOpenOrderStatus(x.Status));
            if (openPositionCount + openOrderCount + active.Count + 1 > _settings.MaximumOpenPositions)
                reasons.Add("Maximum global open position/reservation limit would be exceeded.");
            if (active.Count + 1 > _settings.MaximumPendingReservations)
                reasons.Add("Maximum pending reservation limit would be exceeded.");
            if (active.Count(x => x.InstrumentId.Equals(context.InstrumentId, StringComparison.OrdinalIgnoreCase)) + 1 > _settings.MaximumPendingReservationsPerInstrument)
                reasons.Add("Maximum pending reservations for the instrument would be exceeded.");
            if (active.Count(x => x.StrategyId.Equals(context.StrategyId, StringComparison.OrdinalIgnoreCase)) + 1 > _settings.MaximumPendingReservationsPerStrategy)
                reasons.Add("Maximum pending reservations for the strategy would be exceeded.");
            return reasons;
        }

        private async Task TransitionAsync(Guid reservationId, PortfolioReservationStatus target, string reason, string? brokerOrderId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var reservation = await db.PortfolioRiskReservationRecords.SingleOrDefaultAsync(x => x.Id == reservationId, cancellationToken).ConfigureAwait(false);
                if (reservation == null || !IsActive(reservation.Status)) return;
                var now = _clock.UtcNow;
                var from = reservation.Status;
                reservation.Status = target;
                reservation.UpdatedAtUtc = now;
                reservation.Reason = reason;
                reservation.Version++;
                if (target == PortfolioReservationStatus.Committed)
                {
                    reservation.CommittedAtUtc = now;
                    reservation.BrokerOrderId = brokerOrderId;
                    reservation.ExpiresAtUtc = now.AddSeconds(_settings.CommittedReservationTimeoutSeconds);
                }
                else
                {
                    reservation.ReleasedAtUtc = now;
                }
                AddAudit(db, reservation.Id, from, target, reason, now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task ExpireAsync(TradingBot.Persistence.TradingBotDbContext db, DateTime now, CancellationToken cancellationToken)
        {
            var expired = await db.PortfolioRiskReservationRecords
                .Where(x => (x.Status == PortfolioReservationStatus.Pending || x.Status == PortfolioReservationStatus.Committed) && x.ExpiresAtUtc <= now)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var reservation in expired)
            {
                var from = reservation.Status;
                reservation.Status = PortfolioReservationStatus.Expired;
                reservation.UpdatedAtUtc = now;
                reservation.ReleasedAtUtc = now;
                reservation.Reason = "Reservation timeout expired.";
                reservation.Version++;
                AddAudit(db, reservation.Id, from, PortfolioReservationStatus.Expired, reservation.Reason, now);
            }
            if (expired.Count > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private HashSet<string> FindCorrelationMembers(string instrumentId, string symbol)
        {
            foreach (var group in _settings.CorrelationGroups.Values)
            {
                var members = group.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (members.Contains(instrumentId) || members.Contains(symbol)) return members;
            }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddAudit(TradingBot.Persistence.TradingBotDbContext db, Guid id, PortfolioReservationStatus? from, PortfolioReservationStatus to, string reason, DateTime now) =>
            db.PortfolioRiskReservationAuditRecords.Add(new TradingBot.Persistence.PortfolioRiskReservationAuditRecord
            {
                ReservationId = id,
                FromStatus = from,
                ToStatus = to,
                Reason = reason,
                TimestampUtc = now
            });

        private static bool IsActive(PortfolioReservationStatus status) => status is PortfolioReservationStatus.Pending or PortfolioReservationStatus.Committed;

        private static bool IsOpenOrderStatus(string status) =>
            status.Equals("Submitted", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PreSubmitted", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PendingSubmit", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Open", StringComparison.OrdinalIgnoreCase);

        private static PortfolioReservationResult Rejected(string reason) => new(false, null, 0m, 0m, 0m, new[] { reason });

        private static PortfolioRiskReservationDto ToDto(TradingBot.Persistence.PortfolioRiskReservationRecord x) =>
            new(x.Id, x.AccountId, x.SignalId, x.InstrumentId, x.Symbol, x.StrategyId, x.Quantity, x.PositionValue, x.RiskAmount, x.SignedExposure, x.Status, x.CreatedAtUtc, x.ExpiresAtUtc, x.BrokerOrderId, x.Reason);
    }
}
