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
    public sealed class SignalArbitrationService : ISignalArbitrationService, IDisposable
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IPortfolioRiskService _portfolioRisk;
        private readonly IOrderExecutionService _executionService;
        private readonly SignalArbitrationSettings _settings;
        private readonly IClock _clock;
        private readonly ILogger<SignalArbitrationService> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public SignalArbitrationService(
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IPortfolioRiskService portfolioRisk,
            IOrderExecutionService executionService,
            IOptions<SignalArbitrationSettings> settings,
            IClock clock,
            ILogger<SignalArbitrationService> logger)
        {
            _dbFactory = dbFactory;
            _portfolioRisk = portfolioRisk;
            _executionService = executionService;
            _settings = settings.Value;
            _clock = clock;
            _logger = logger;
            _executionService.OrderStatusUpdated += OnOrderUpdateAsync;
            _executionService.OrderFilled += OnOrderUpdateAsync;
        }

        public async Task<SignalArbitrationResult> ArbitrateAsync(
            PatternCandidate pattern,
            string accountId,
            IReadOnlyList<PositionDto> currentPositions,
            CancellationToken cancellationToken = default)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (string.IsNullOrWhiteSpace(accountId)) return Rejected("Verified broker account is required for signal arbitration.");
            if (currentPositions == null) return Rejected("Current broker positions are required for signal arbitration.");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                var now = _clock.UtcNow;
                var reservationsToRelease = await ExpirePendingAsync(db, now, cancellationToken).ConfigureAwait(false);
                var key = $"{accountId.Trim().ToUpperInvariant()}|{pattern.Context.SignalId:N}";
                var existing = await db.SignalArbitrationRecords.SingleOrDefaultAsync(x => x.ArbitrationKey == key, cancellationToken).ConfigureAwait(false);
                if (existing != null)
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    await ReleaseReservationsAsync(reservationsToRelease, "Signal arbitration intent expired.", cancellationToken).ConfigureAwait(false);
                    return IsActive(existing.Status)
                        ? new SignalArbitrationResult(true, existing.Id, "Signal already has an active arbitration intent.")
                        : Rejected("Signal already has a terminal arbitration decision.");
                }

                var active = await db.SignalArbitrationRecords
                    .Where(x => x.AccountId == accountId
                        && x.InstrumentId == pattern.InstrumentId
                        && (x.Status == SignalArbitrationStatus.Accepted
                            || x.Status == SignalArbitrationStatus.Reserved
                            || x.Status == SignalArbitrationStatus.Submitting
                            || x.Status == SignalArbitrationStatus.Submitted
                            || x.Status == SignalArbitrationStatus.PartiallyFilled
                            || x.Status == SignalArbitrationStatus.Filled))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var reason = ValidateBrokerDirection(pattern, accountId, currentPositions);
                if (reason == null && active.Count >= _settings.MaximumActiveAllocationsPerInstrument)
                    reason = "Maximum active virtual allocations for the instrument has been reached.";

                var sameDirection = active.Where(x => x.Direction == pattern.Direction).ToArray();
                if (reason == null && !_settings.AllowSameDirectionScaleIn && sameDirection.Length > 0)
                    reason = "Same-direction scale-in is disabled for this instrument.";
                if (reason == null && _settings.RejectSameStrategyWhileActive
                    && sameDirection.Any(x => x.StrategyId.Equals(pattern.StrategyId, StringComparison.OrdinalIgnoreCase)))
                    reason = "The same strategy already has an active allocation for this instrument and direction.";

                var opposing = active.Where(x => x.Direction != pattern.Direction).ToArray();
                if (reason == null && opposing.Length > 0)
                {
                    if (_settings.ConflictPolicy == SignalConflictPolicy.Reject)
                    {
                        reason = "An opposing active signal exists and conflict policy is Reject.";
                    }
                    else
                    {
                        var candidatePriority = GetPriority(pattern.StrategyId);
                        if (opposing.Any(x => x.Status is not (SignalArbitrationStatus.Accepted or SignalArbitrationStatus.Reserved)))
                        {
                            reason = "An opposing signal is already being submitted or has broker exposure; priority cannot displace it.";
                        }
                        else if (opposing.Any(x => x.Priority >= candidatePriority))
                        {
                            reason = "The opposing signal has equal or higher strategy priority.";
                        }
                        else
                        {
                            foreach (var displaced in opposing)
                            {
                                var from = displaced.Status;
                                displaced.Status = SignalArbitrationStatus.Superseded;
                                displaced.Reason = $"Superseded by higher-priority signal {pattern.Context.SignalId}.";
                                displaced.UpdatedAtUtc = now;
                                displaced.Version++;
                                AddAudit(db, displaced.Id, from, displaced.Status, displaced.Reason, now);
                                if (displaced.ReservationId.HasValue) reservationsToRelease.Add(displaced.ReservationId.Value);
                            }
                        }
                    }
                }

                if (reason != null)
                {
                    var rejected = CreateRecord(pattern, accountId, SignalArbitrationStatus.Rejected, reason, now);
                    db.SignalArbitrationRecords.Add(rejected);
                    AddAudit(db, rejected.Id, null, rejected.Status, reason, now);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    await ReleaseReservationsAsync(reservationsToRelease, "Signal superseded or expired during arbitration.", cancellationToken).ConfigureAwait(false);
                    return Rejected(reason);
                }

                var accepted = CreateRecord(pattern, accountId, SignalArbitrationStatus.Accepted, "Signal arbitration accepted.", now);
                db.SignalArbitrationRecords.Add(accepted);
                AddAudit(db, accepted.Id, null, accepted.Status, accepted.Reason, now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await ReleaseReservationsAsync(reservationsToRelease, "Signal superseded or expired during arbitration.", cancellationToken).ConfigureAwait(false);
                return new SignalArbitrationResult(true, accepted.Id, accepted.Reason);
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task BindReservationAsync(Guid arbitrationId, Guid reservationId, decimal approvedQuantity, decimal positionValue, CancellationToken cancellationToken = default) =>
            MutateAsync(arbitrationId, cancellationToken, record =>
            {
                if (record.Status != SignalArbitrationStatus.Accepted) throw new InvalidOperationException("Only an accepted arbitration intent can bind a risk reservation.");
                if (approvedQuantity <= 0m) throw new ArgumentOutOfRangeException(nameof(approvedQuantity));
                if (positionValue <= 0m) throw new ArgumentOutOfRangeException(nameof(positionValue));
                record.ReservationId = reservationId;
                record.ApprovedQuantity = approvedQuantity;
                record.PositionValue = positionValue;
                return (SignalArbitrationStatus.Reserved, "Portfolio risk reservation bound to arbitration intent.");
            });

        public async Task<SignalExecutionClaimResult> ClaimForExecutionAsync(Guid arbitrationId, CancellationToken cancellationToken = default)
        {
            try
            {
                await MutateAsync(arbitrationId, cancellationToken, record =>
                {
                    if (record.Status != SignalArbitrationStatus.Reserved)
                        throw new InvalidOperationException($"Arbitration intent cannot execute from status {record.Status}.");
                    if (!record.ReservationId.HasValue || record.ApprovedQuantity <= 0m)
                        throw new InvalidOperationException("Arbitration intent has no valid risk reservation.");
                    return (SignalArbitrationStatus.Submitting, "Execution atomically claimed the arbitration intent.");
                }).ConfigureAwait(false);
                return new SignalExecutionClaimResult(true, "Execution claim accepted.");
            }
            catch (InvalidOperationException ex)
            {
                return new SignalExecutionClaimResult(false, ex.Message);
            }
        }

        public async Task RecordSubmissionAsync(Guid arbitrationId, ManagedOrderResult result, CancellationToken cancellationToken = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var record = await db.SignalArbitrationRecords.SingleAsync(x => x.Id == arbitrationId, cancellationToken).ConfigureAwait(false);
                if (record.Status != SignalArbitrationStatus.Submitting) return;
                var accepted = result.Submitted || result.Status == OrderStatus.PendingBrokerConfirmation;
                var target = accepted
                    ? result.FilledQuantity > 0m
                        ? result.RemainingQuantity > 0m ? SignalArbitrationStatus.PartiallyFilled : SignalArbitrationStatus.Filled
                        : SignalArbitrationStatus.Submitted
                    : SignalArbitrationStatus.Rejected;
                var from = record.Status;
                record.Status = target;
                record.EntryBrokerOrderId = result.BrokerOrderId;
                record.FilledQuantity = Math.Min(record.ApprovedQuantity, Math.Max(record.FilledQuantity, result.FilledQuantity));
                record.Reason = accepted ? $"Broker entry status: {result.Status}." : $"Broker rejected entry: {result.Message}";
                record.UpdatedAtUtc = _clock.UtcNow;
                record.Version++;
                AddAudit(db, record.Id, from, target, record.Reason, record.UpdatedAtUtc);
                AddOrder(db, record.Id, result.BrokerOrderId, true, record.ApprovedQuantity, result.FilledQuantity, result.Status.ToString(), record.UpdatedAtUtc);
                foreach (var childId in result.ChildOrderIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    AddOrder(db, record.Id, childId, false, record.ApprovedQuantity, 0m, OrderStatus.Submitted.ToString(), record.UpdatedAtUtc);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RecordEntryFillAsync(Guid signalId, decimal cumulativeFilledQuantity, CancellationToken cancellationToken = default)
        {
            if (cumulativeFilledQuantity <= 0m) return;
            await UpdateFillBySignalAsync(signalId, cumulativeFilledQuantity, cancellationToken).ConfigureAwait(false);
        }

        public async Task RegisterExitOrderAsync(Guid signalId, string brokerOrderId, decimal quantity, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(brokerOrderId)) return;
            if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
            brokerOrderId = brokerOrderId.Trim();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var record = await db.SignalArbitrationRecords.SingleAsync(x => x.SignalId == signalId
                    && (x.Status == SignalArbitrationStatus.Submitted || x.Status == SignalArbitrationStatus.PartiallyFilled || x.Status == SignalArbitrationStatus.Filled), cancellationToken).ConfigureAwait(false);
                var existing = await db.VirtualAllocationOrderRecords
                    .SingleOrDefaultAsync(x => x.BrokerOrderId == brokerOrderId, cancellationToken)
                    .ConfigureAwait(false);
                if (existing != null)
                {
                    if (existing.ArbitrationId != record.Id || existing.IsEntry)
                        throw new InvalidOperationException($"Broker order {brokerOrderId} is already attributed to another virtual allocation.");

                    existing.RequestedQuantity = Math.Max(existing.RequestedQuantity, quantity);
                    existing.UpdatedAtUtc = _clock.UtcNow;
                }
                else
                {
                    AddOrder(db, record.Id, brokerOrderId, false, quantity, 0m, OrderStatus.Submitted.ToString(), _clock.UtcNow);
                }
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<SignalExecutionClaimResult> ValidateExitQuantityAsync(Guid signalId, decimal quantity, CancellationToken cancellationToken = default)
        {
            if (quantity <= 0m) return new SignalExecutionClaimResult(false, "Exit quantity must be positive.");
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.SignalArbitrationRecords.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SignalId == signalId
                    && (x.Status == SignalArbitrationStatus.Submitted || x.Status == SignalArbitrationStatus.PartiallyFilled || x.Status == SignalArbitrationStatus.Filled), cancellationToken).ConfigureAwait(false);
            if (record == null) return new SignalExecutionClaimResult(false, "No active virtual allocation exists for this signal.");
            var exited = await db.VirtualAllocationOrderRecords.AsNoTracking()
                .Where(x => x.ArbitrationId == record.Id && !x.IsEntry)
                .SumAsync(x => x.FilledQuantity, cancellationToken).ConfigureAwait(false);
            var open = Math.Max(0m, record.FilledQuantity - exited);
            return quantity <= open
                ? new SignalExecutionClaimResult(true, "Exit quantity is within this signal's virtual allocation.")
                : new SignalExecutionClaimResult(false, $"Exit quantity {quantity} exceeds virtual open quantity {open}.");
        }

        public async Task RejectAsync(Guid arbitrationId, string reason, CancellationToken cancellationToken = default)
        {
            Guid? reservationId = null;
            await MutateAsync(arbitrationId, cancellationToken, record =>
            {
                if (!IsActive(record.Status)) return (record.Status, record.Reason);
                reservationId = record.ReservationId;
                return (SignalArbitrationStatus.Rejected, reason);
            }).ConfigureAwait(false);
            if (reservationId.HasValue) await _portfolioRisk.ReleaseAsync(reservationId.Value, reason, cancellationToken).ConfigureAwait(false);
        }

        public Task CompleteAnalysisOnlyAsync(Guid arbitrationId, CancellationToken cancellationToken = default) =>
            MutateAsync(arbitrationId, cancellationToken, record => (SignalArbitrationStatus.AnalysisOnly, "Analysis-only plan completed without broker exposure."));

        public async Task<SignalArbitrationStateDto> GetStateAsync(string? accountId = null, string? instrumentId = null, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var query = db.SignalArbitrationRecords.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(accountId)) query = query.Where(x => x.AccountId == accountId);
            if (!string.IsNullOrWhiteSpace(instrumentId)) query = query.Where(x => x.InstrumentId == instrumentId);
            var records = await query.OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = records.Select(x => x.Id).ToArray();
            var orders = await db.VirtualAllocationOrderRecords.AsNoTracking().Where(x => ids.Contains(x.ArbitrationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var items = records.Select(x =>
            {
                var allocationOrders = orders.Where(o => o.ArbitrationId == x.Id).ToArray();
                var exited = allocationOrders.Where(o => !o.IsEntry).Sum(o => o.FilledQuantity);
                return new VirtualAllocationDto(x.Id, x.AccountId, x.SignalId, x.InstrumentId, x.Symbol, x.StrategyId, x.Direction, x.Priority, x.Status,
                    x.ReservationId, x.ApprovedQuantity, x.PositionValue, x.FilledQuantity, exited, Math.Max(0m, x.FilledQuantity - exited), x.EntryBrokerOrderId,
                    allocationOrders.Where(o => !o.IsEntry).Select(o => o.BrokerOrderId).ToArray(), x.CreatedAtUtc, x.UpdatedAtUtc, x.Reason);
            }).ToArray();
            return new SignalArbitrationStateDto(accountId, instrumentId, items);
        }

        private async Task OnOrderUpdateAsync(OrderStatusDto status)
        {
            var brokerOrderId = status.BrokerOrderId ?? status.OrderId;
            if (string.IsNullOrWhiteSpace(brokerOrderId) || status.FilledQuantity <= 0m) return;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
                var order = await db.VirtualAllocationOrderRecords.SingleOrDefaultAsync(x => x.BrokerOrderId == brokerOrderId).ConfigureAwait(false);
                if (order == null || status.FilledQuantity <= order.FilledQuantity) return;
                order.FilledQuantity = Math.Min(order.RequestedQuantity, status.FilledQuantity);
                order.Status = status.Status;
                order.UpdatedAtUtc = _clock.UtcNow;
                var record = await db.SignalArbitrationRecords.SingleAsync(x => x.Id == order.ArbitrationId).ConfigureAwait(false);
                if (order.IsEntry)
                {
                    record.FilledQuantity = Math.Min(record.ApprovedQuantity, Math.Max(record.FilledQuantity, order.FilledQuantity));
                    var target = status.RemainingQuantity > 0m ? SignalArbitrationStatus.PartiallyFilled : SignalArbitrationStatus.Filled;
                    if (record.Status != target)
                    {
                        AddAudit(db, record.Id, record.Status, target, $"Entry fill updated to {record.FilledQuantity}.", _clock.UtcNow);
                        record.Status = target;
                    }
                }
                else
                {
                    var exited = await db.VirtualAllocationOrderRecords.Where(x => x.ArbitrationId == record.Id && !x.IsEntry).SumAsync(x => x.FilledQuantity).ConfigureAwait(false);
                    record.ExitedQuantity = Math.Min(record.FilledQuantity, exited);
                    if (exited >= record.FilledQuantity && record.FilledQuantity > 0m)
                    {
                        AddAudit(db, record.Id, record.Status, SignalArbitrationStatus.Closed, "Virtual allocation fully exited.", _clock.UtcNow);
                        record.Status = SignalArbitrationStatus.Closed;
                    }
                }
                record.UpdatedAtUtc = _clock.UtcNow;
                record.Version++;
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update virtual allocation from broker order {BrokerOrderId}.", brokerOrderId);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task UpdateFillBySignalAsync(Guid signalId, decimal cumulativeFilledQuantity, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var record = await db.SignalArbitrationRecords.SingleAsync(x => x.SignalId == signalId
                    && (x.Status == SignalArbitrationStatus.Submitted || x.Status == SignalArbitrationStatus.PartiallyFilled || x.Status == SignalArbitrationStatus.Filled), cancellationToken).ConfigureAwait(false);
                if (cumulativeFilledQuantity <= record.FilledQuantity) return;
                record.FilledQuantity = Math.Min(record.ApprovedQuantity, cumulativeFilledQuantity);
                var target = record.FilledQuantity >= record.ApprovedQuantity ? SignalArbitrationStatus.Filled : SignalArbitrationStatus.PartiallyFilled;
                AddAudit(db, record.Id, record.Status, target, $"Entry fill updated to {record.FilledQuantity}.", _clock.UtcNow);
                record.Status = target;
                record.UpdatedAtUtc = _clock.UtcNow;
                record.Version++;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task MutateAsync(Guid id, CancellationToken cancellationToken, Func<TradingBot.Persistence.SignalArbitrationRecord, (SignalArbitrationStatus Status, string Reason)> mutation)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var record = await db.SignalArbitrationRecords.SingleAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
                var from = record.Status;
                var change = mutation(record);
                if (change.Status == from && change.Reason == record.Reason) return;
                record.Status = change.Status;
                record.Reason = change.Reason;
                record.UpdatedAtUtc = _clock.UtcNow;
                record.Version++;
                AddAudit(db, id, from, change.Status, change.Reason, record.UpdatedAtUtc);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<List<Guid>> ExpirePendingAsync(TradingBot.Persistence.TradingBotDbContext db, DateTime now, CancellationToken cancellationToken)
        {
            var expired = await db.SignalArbitrationRecords
                .Where(x => (x.Status == SignalArbitrationStatus.Accepted || x.Status == SignalArbitrationStatus.Reserved) && x.ExpiresAtUtc <= now)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var reservations = new List<Guid>();
            foreach (var record in expired)
            {
                var from = record.Status;
                record.Status = SignalArbitrationStatus.Expired;
                record.Reason = "Arbitration intent expired before execution claim.";
                record.UpdatedAtUtc = now;
                record.Version++;
                AddAudit(db, record.Id, from, record.Status, record.Reason, now);
                if (record.ReservationId.HasValue) reservations.Add(record.ReservationId.Value);
            }
            return reservations;
        }

        private async Task ReleaseReservationsAsync(IEnumerable<Guid> reservationIds, string reason, CancellationToken cancellationToken)
        {
            foreach (var id in reservationIds.Distinct())
                await _portfolioRisk.ReleaseAsync(id, reason, cancellationToken).ConfigureAwait(false);
        }

        private TradingBot.Persistence.SignalArbitrationRecord CreateRecord(PatternCandidate pattern, string accountId, SignalArbitrationStatus status, string reason, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            ArbitrationKey = $"{accountId.Trim().ToUpperInvariant()}|{pattern.Context.SignalId:N}",
            AccountId = accountId.Trim(),
            CorrelationId = pattern.Context.CorrelationId,
            SignalId = pattern.Context.SignalId,
            InstrumentId = pattern.InstrumentId,
            Symbol = pattern.Symbol.Trim().ToUpperInvariant(),
            StrategyId = pattern.StrategyId,
            Direction = pattern.Direction,
            Priority = GetPriority(pattern.StrategyId),
            Confidence = pattern.Confidence,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(_settings.IntentTimeoutSeconds),
            Reason = reason,
            Version = 1
        };

        private string? ValidateBrokerDirection(PatternCandidate pattern, string accountId, IReadOnlyList<PositionDto> positions)
        {
            var quantity = positions
                .Where(x => (string.IsNullOrWhiteSpace(x.AccountId) || x.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                    && x.Symbol.Equals(pattern.Symbol, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.Quantity);
            if (quantity > 0m && pattern.Direction == TradeDirection.Short) return "Short signal conflicts with the broker's existing long net position.";
            if (quantity < 0m && pattern.Direction == TradeDirection.Long) return "Long signal conflicts with the broker's existing short net position.";
            return null;
        }

        private int GetPriority(string strategyId) => _settings.StrategyPriorities.TryGetValue(strategyId, out var priority) ? priority : 0;

        private static bool IsActive(SignalArbitrationStatus status) => status is SignalArbitrationStatus.Accepted or SignalArbitrationStatus.Reserved
            or SignalArbitrationStatus.Submitting or SignalArbitrationStatus.Submitted or SignalArbitrationStatus.PartiallyFilled or SignalArbitrationStatus.Filled;

        private static SignalArbitrationResult Rejected(string reason) => new(false, null, reason);

        private static void AddAudit(TradingBot.Persistence.TradingBotDbContext db, Guid id, SignalArbitrationStatus? from, SignalArbitrationStatus to, string reason, DateTime now) =>
            db.SignalArbitrationAuditRecords.Add(new TradingBot.Persistence.SignalArbitrationAuditRecord { ArbitrationId = id, FromStatus = from, ToStatus = to, Reason = reason, TimestampUtc = now });

        private static void AddOrder(TradingBot.Persistence.TradingBotDbContext db, Guid id, string? brokerOrderId, bool isEntry, decimal requested, decimal filled, string status, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(brokerOrderId)) return;
            if (db.VirtualAllocationOrderRecords.Local.Any(x => x.BrokerOrderId == brokerOrderId)) return;
            db.VirtualAllocationOrderRecords.Add(new TradingBot.Persistence.VirtualAllocationOrderRecord
            {
                ArbitrationId = id,
                BrokerOrderId = brokerOrderId,
                IsEntry = isEntry,
                RequestedQuantity = requested,
                FilledQuantity = Math.Min(requested, Math.Max(0m, filled)),
                Status = status,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        public void Dispose()
        {
            _executionService.OrderStatusUpdated -= OnOrderUpdateAsync;
            _executionService.OrderFilled -= OnOrderUpdateAsync;
            _gate.Dispose();
        }
    }
}
