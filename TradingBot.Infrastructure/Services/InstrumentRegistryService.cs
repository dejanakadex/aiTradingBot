using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public sealed class InstrumentRegistryService : IInstrumentRegistryService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly TradingSettings _settings;
        private readonly IClock _clock;

        public InstrumentRegistryService(
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IOptions<TradingSettings> settings,
            IClock clock)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task<InstrumentRegistrySyncResult> SynchronizeConfiguredAsync(CancellationToken cancellationToken = default)
        {
            var configured = _settings.GetConfiguredInstruments();
            var configuredIds = configured.Select(item => item.InstrumentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = ToUtc(_clock.UtcNow);
            var added = 0;
            var updated = 0;
            var disabled = 0;
            var unchanged = 0;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.InstrumentRegistryRecords.ToListAsync(cancellationToken).ConfigureAwait(false);
            var byInstrumentId = records.ToDictionary(item => item.InstrumentId, StringComparer.OrdinalIgnoreCase);

            foreach (var instrument in configured)
            {
                var configurationHash = BuildConfigurationHash(instrument);
                if (!byInstrumentId.TryGetValue(instrument.InstrumentId, out var record))
                {
                    var initialStatus = instrument.Enabled
                        ? InstrumentOnboardingStatus.BackfillPending
                        : InstrumentOnboardingStatus.Disabled;
                    record = new InstrumentRegistryRecord
                    {
                        InstrumentId = instrument.InstrumentId,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        StatusChangedAtUtc = now,
                        Status = initialStatus,
                        StatusReason = instrument.Enabled
                            ? "Instrument added from configuration; historical backfill is pending."
                            : "Instrument is disabled in configuration.",
                        Version = 1
                    };
                    ApplyConfiguration(record, instrument, configurationHash);
                    db.InstrumentRegistryRecords.Add(record);
                    AddTransition(db, record.InstrumentId, null, initialStatus, record.StatusReason, "ConfigurationSync", now);
                    byInstrumentId.Add(record.InstrumentId, record);
                    added++;
                    continue;
                }

                var previousStatus = record.Status;
                var dataConfigurationChanged = !string.Equals(record.ConfigurationHash, configurationHash, StringComparison.Ordinal);
                var recordChanged = ApplyConfiguration(record, instrument, configurationHash);
                string? transitionReason = null;

                if (dataConfigurationChanged)
                {
                    record.BrokerContractId = null;
                    record.BrokerPrimaryExchange = string.Empty;
                }

                if (!instrument.Enabled && record.Status != InstrumentOnboardingStatus.Disabled)
                {
                    record.Status = InstrumentOnboardingStatus.Disabled;
                    transitionReason = "Instrument disabled by configuration sync.";
                }
                else if (instrument.Enabled && previousStatus == InstrumentOnboardingStatus.Disabled)
                {
                    record.Status = InstrumentOnboardingStatus.BackfillPending;
                    transitionReason = "Instrument re-enabled; historical backfill must be verified.";
                }
                else if (instrument.Enabled && dataConfigurationChanged)
                {
                    record.Status = InstrumentOnboardingStatus.BackfillPending;
                    transitionReason = "Instrument data configuration changed; onboarding reset to backfill pending.";
                }

                if (transitionReason != null)
                {
                    record.StatusReason = transitionReason;
                    record.StatusChangedAtUtc = now;
                    AddTransition(db, record.InstrumentId, previousStatus, record.Status, transitionReason, "ConfigurationSync", now);
                    recordChanged = true;
                }

                if (recordChanged)
                {
                    record.UpdatedAtUtc = now;
                    record.Version++;
                    if (record.Status == InstrumentOnboardingStatus.Disabled && previousStatus != InstrumentOnboardingStatus.Disabled)
                    {
                        disabled++;
                    }
                    else
                    {
                        updated++;
                    }
                }
                else
                {
                    unchanged++;
                }
            }

            foreach (var record in records.Where(item => !configuredIds.Contains(item.InstrumentId)))
            {
                var wasAlreadyRemoved = !record.ConfiguredEnabled
                    && !record.TradingRequested
                    && record.Status == InstrumentOnboardingStatus.Disabled
                    && record.StatusReason == "Instrument is no longer present in configuration.";
                if (wasAlreadyRemoved)
                {
                    unchanged++;
                    continue;
                }

                var previousStatus = record.Status;
                record.ConfiguredEnabled = false;
                record.TradingRequested = false;
                record.Status = InstrumentOnboardingStatus.Disabled;
                record.StatusReason = "Instrument is no longer present in configuration.";
                record.StatusChangedAtUtc = now;
                record.UpdatedAtUtc = now;
                record.Version++;
                AddTransition(db, record.InstrumentId, previousStatus, record.Status, record.StatusReason, "ConfigurationSync", now);
                disabled++;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var snapshots = await GetAllAsync(cancellationToken).ConfigureAwait(false);
            return new InstrumentRegistrySyncResult(added, updated, disabled, unchanged, snapshots);
        }

        public async Task<IReadOnlyList<InstrumentRegistrySnapshot>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.InstrumentRegistryRecords
                .AsNoTracking()
                .OrderBy(item => item.Symbol)
                .ThenBy(item => item.InstrumentId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        public async Task<InstrumentRegistrySnapshot?> GetAsync(string instrumentId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(instrumentId)) throw new ArgumentException("Instrument ID is required.", nameof(instrumentId));

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var normalized = instrumentId.Trim();
            var record = await db.InstrumentRegistryRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.InstrumentId == normalized, cancellationToken)
                .ConfigureAwait(false);
            return record == null ? null : ToSnapshot(record);
        }

        public async Task<InstrumentRegistrySnapshot> SetBrokerContractAsync(
            string instrumentId,
            int expectedVersion,
            long brokerContractId,
            string brokerPrimaryExchange,
            CancellationToken cancellationToken = default)
        {
            if (brokerContractId <= 0) throw new ArgumentOutOfRangeException(nameof(brokerContractId));
            if (string.IsNullOrWhiteSpace(brokerPrimaryExchange)) throw new ArgumentException("Broker primary exchange is required.", nameof(brokerPrimaryExchange));

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await GetRequiredTrackedAsync(db, instrumentId, cancellationToken).ConfigureAwait(false);
            if (record.Version != expectedVersion)
            {
                throw new InvalidOperationException($"Instrument '{record.InstrumentId}' version is {record.Version}, expected {expectedVersion}.");
            }
            if (!record.ConfiguredEnabled || record.Status == InstrumentOnboardingStatus.Disabled)
            {
                throw new InvalidOperationException($"Cannot assign broker metadata to disabled instrument '{record.InstrumentId}'.");
            }

            var primaryExchange = brokerPrimaryExchange.Trim().ToUpperInvariant();
            if (record.BrokerContractId == brokerContractId
                && string.Equals(record.BrokerPrimaryExchange, primaryExchange, StringComparison.Ordinal))
            {
                return ToSnapshot(record);
            }

            record.BrokerContractId = brokerContractId;
            record.BrokerPrimaryExchange = primaryExchange;
            record.UpdatedAtUtc = ToUtc(_clock.UtcNow);
            record.Version++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }

        public async Task<InstrumentRegistrySnapshot> TransitionAsync(
            string instrumentId,
            InstrumentOnboardingStatus expectedStatus,
            InstrumentOnboardingStatus targetStatus,
            string reason,
            string trigger,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Transition reason is required.", nameof(reason));
            if (string.IsNullOrWhiteSpace(trigger)) throw new ArgumentException("Transition trigger is required.", nameof(trigger));

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await GetRequiredTrackedAsync(db, instrumentId, cancellationToken).ConfigureAwait(false);
            if (record.Status != expectedStatus)
            {
                throw new InvalidOperationException($"Instrument '{record.InstrumentId}' status is {record.Status}, expected {expectedStatus}.");
            }
            if (record.Status == targetStatus)
            {
                return ToSnapshot(record);
            }
            if (!record.ConfiguredEnabled && targetStatus != InstrumentOnboardingStatus.Disabled)
            {
                throw new InvalidOperationException($"Disabled instrument '{record.InstrumentId}' cannot transition to {targetStatus}.");
            }
            if (!InstrumentOnboardingTransitions.IsAllowed(record.Status, targetStatus))
            {
                throw new InvalidOperationException($"Transition {record.Status} -> {targetStatus} is not allowed for '{record.InstrumentId}'.");
            }
            if (targetStatus == InstrumentOnboardingStatus.LiveEnabled && !record.TradingRequested)
            {
                throw new InvalidOperationException($"Instrument '{record.InstrumentId}' cannot become LiveEnabled because trading was not requested in configuration.");
            }

            var now = ToUtc(_clock.UtcNow);
            var previousStatus = record.Status;
            record.Status = targetStatus;
            record.StatusReason = reason.Trim();
            record.StatusChangedAtUtc = now;
            record.UpdatedAtUtc = now;
            record.Version++;
            AddTransition(db, record.InstrumentId, previousStatus, targetStatus, record.StatusReason, trigger.Trim(), now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }

        private static bool ApplyConfiguration(InstrumentRegistryRecord record, ConfiguredInstrument instrument, string configurationHash)
        {
            var allowedDirectionsJson = JsonSerializer.Serialize(instrument.AllowedDirections, JsonOptions);
            var strategyIdsJson = JsonSerializer.Serialize(instrument.StrategyIds, JsonOptions);
            var timeframesJson = JsonSerializer.Serialize(instrument.MarketDataTimeframes, JsonOptions);
            var changed = record.Symbol != instrument.Symbol
                || record.Exchange != instrument.Exchange
                || record.Currency != instrument.Currency
                || record.SecurityType != instrument.SecurityType
                || record.ConfiguredEnabled != instrument.Enabled
                || record.TradingRequested != instrument.TradingEnabled
                || record.AllowedDirectionsJson != allowedDirectionsJson
                || record.StrategyIdsJson != strategyIdsJson
                || record.MarketDataTimeframesJson != timeframesJson
                || record.MaximumPositionValue != instrument.MaximumPositionValue
                || record.MaximumHoldingSeconds != instrument.MaximumHoldingSeconds
                || record.ConfigurationHash != configurationHash;

            record.Symbol = instrument.Symbol;
            record.Exchange = instrument.Exchange;
            record.Currency = instrument.Currency;
            record.SecurityType = instrument.SecurityType;
            record.ConfiguredEnabled = instrument.Enabled;
            record.TradingRequested = instrument.TradingEnabled;
            record.AllowedDirectionsJson = allowedDirectionsJson;
            record.StrategyIdsJson = strategyIdsJson;
            record.MarketDataTimeframesJson = timeframesJson;
            record.MaximumPositionValue = instrument.MaximumPositionValue;
            record.MaximumHoldingSeconds = instrument.MaximumHoldingSeconds;
            record.ConfigurationHash = configurationHash;
            return changed;
        }

        private static string BuildConfigurationHash(ConfiguredInstrument instrument)
        {
            var canonical = string.Join('|', new[]
            {
                instrument.InstrumentId,
                instrument.Symbol,
                instrument.Exchange,
                instrument.Currency,
                instrument.SecurityType,
                string.Join(',', instrument.AllowedDirections.OrderBy(value => value)),
                string.Join(',', instrument.StrategyIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                string.Join(',', instrument.MarketDataTimeframes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                instrument.MaximumPositionValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                instrument.MaximumHoldingSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static InstrumentRegistrySnapshot ToSnapshot(InstrumentRegistryRecord record)
        {
            return new InstrumentRegistrySnapshot
            {
                InstrumentId = record.InstrumentId,
                Symbol = record.Symbol,
                Exchange = record.Exchange,
                Currency = record.Currency,
                SecurityType = record.SecurityType,
                ConfiguredEnabled = record.ConfiguredEnabled,
                TradingRequested = record.TradingRequested,
                Status = record.Status,
                AllowedDirections = DeserializeArray<TradeDirection>(record.AllowedDirectionsJson),
                StrategyIds = DeserializeArray<string>(record.StrategyIdsJson),
                MarketDataTimeframes = DeserializeArray<string>(record.MarketDataTimeframesJson),
                MaximumPositionValue = record.MaximumPositionValue,
                MaximumHoldingSeconds = record.MaximumHoldingSeconds,
                BrokerContractId = record.BrokerContractId,
                BrokerPrimaryExchange = record.BrokerPrimaryExchange,
                StatusReason = record.StatusReason,
                CreatedAtUtc = ToUtc(record.CreatedAtUtc),
                UpdatedAtUtc = ToUtc(record.UpdatedAtUtc),
                StatusChangedAtUtc = ToUtc(record.StatusChangedAtUtc),
                Version = record.Version
            };
        }

        private static IReadOnlyList<T> DeserializeArray<T>(string json)
        {
            return JsonSerializer.Deserialize<T[]>(json, JsonOptions) ?? Array.Empty<T>();
        }

        private static async Task<InstrumentRegistryRecord> GetRequiredTrackedAsync(
            TradingBotDbContext db,
            string instrumentId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(instrumentId)) throw new ArgumentException("Instrument ID is required.", nameof(instrumentId));
            var normalized = instrumentId.Trim();
            return await db.InstrumentRegistryRecords
                .SingleOrDefaultAsync(item => item.InstrumentId == normalized, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Instrument '{normalized}' is not registered.");
        }

        private static void AddTransition(
            TradingBotDbContext db,
            string instrumentId,
            InstrumentOnboardingStatus? fromStatus,
            InstrumentOnboardingStatus toStatus,
            string reason,
            string trigger,
            DateTime timestampUtc)
        {
            db.InstrumentStatusTransitionRecords.Add(new InstrumentStatusTransitionRecord
            {
                InstrumentId = instrumentId,
                FromStatus = fromStatus,
                ToStatus = toStatus,
                Reason = reason,
                Trigger = trigger,
                TimestampUtc = timestampUtc
            });
        }

        private static DateTime ToUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
                _ => value.ToUniversalTime()
            };
        }
    }
}
