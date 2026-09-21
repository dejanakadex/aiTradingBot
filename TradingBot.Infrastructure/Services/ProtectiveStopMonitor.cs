using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services
{
    public sealed class ProtectiveStopMonitor : IProtectiveStopMonitor
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IOrderExecutionService _orderExecutionService;
        private readonly ITradingEngineStatusService _statusService;
        private readonly TradingSettings _settings;
        private readonly ILogger<ProtectiveStopMonitor> _logger;

        public ProtectiveStopMonitor(
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IOrderExecutionService orderExecutionService,
            ITradingEngineStatusService statusService,
            IOptions<TradingSettings> settings,
            ILogger<ProtectiveStopMonitor> logger)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _orderExecutionService = orderExecutionService ?? throw new ArgumentNullException(nameof(orderExecutionService));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _settings = settings?.Value ?? new TradingSettings();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
        {
            if (!_settings.RequireProtectiveStopForBotPositions) return true;

            try
            {
                var brokerOpenIds = (await _orderExecutionService.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false))
                    .Select(o => o.BrokerOrderId ?? o.OrderId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var openTrades = await db.Trades.AsNoTracking()
                    .Where(t => !t.ClosedUtc.HasValue && t.Size != 0m)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (openTrades.Count == 0) return true;

                var openLocalStops = await db.OrderRecords.AsNoTracking()
                    .Where(o => o.Status == OrderStatus.New
                        || o.Status == OrderStatus.PendingBrokerConfirmation
                        || o.Status == OrderStatus.Unknown
                        || o.Status == OrderStatus.Submitted
                        || o.Status == OrderStatus.PartiallyFilled)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var mismatches = new List<string>();
                foreach (var trade in openTrades)
                {
                    var stop = openLocalStops.FirstOrDefault(o =>
                        string.Equals(o.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase)
                        && IsProtectiveStop(o.RawJson)
                        && !string.IsNullOrWhiteSpace(o.BrokerOrderId)
                        && brokerOpenIds.Contains(o.BrokerOrderId));

                    if (stop == null)
                    {
                        mismatches.Add($"Open bot position {trade.Symbol} size {trade.Size} has no verified broker-side protective stop.");
                    }
                }

                if (mismatches.Count == 0) return true;

                foreach (var mismatch in mismatches)
                {
                    _logger.LogError("Protective stop invariant failed: {Mismatch}", mismatch);
                }

                _statusService.SetState(TradingEngineState.Degraded, false, "Protective stop invariant failed. Trading disabled.", mismatches);
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or JsonException)
            {
                _logger.LogError(ex, "Protective stop monitor failed. Trading must fail closed.");
                _statusService.SetState(TradingEngineState.Degraded, false, "Protective stop monitor failed. Trading disabled.", new[] { ex.Message });
                return false;
            }
        }

        private static bool IsProtectiveStop(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return false;
            if (rawJson.Contains("ProtectiveStop", StringComparison.OrdinalIgnoreCase)) return true;
            if (rawJson.Contains("\"type\":\"STOP\"", StringComparison.OrdinalIgnoreCase)) return true;
            return rawJson.Contains("\"Type\":\"Stop\"", StringComparison.OrdinalIgnoreCase);
        }
    }
}
