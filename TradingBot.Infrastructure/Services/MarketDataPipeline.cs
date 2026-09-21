using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Models;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public class MarketDataPipeline : IDisposable
    {
        private readonly IIbkrAdapter _adapter;
        private readonly ITradingEventBus _eventBus;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IMarketDataValidator _validator;
        private readonly IExitManagementService? _exitManagementService;
        private readonly ITradingPipelineStatusService? _pipelineStatusService;
        private readonly ILogger<MarketDataPipeline> _logger;

        public MarketDataPipeline(IIbkrAdapter adapter,
            ITradingEventBus eventBus,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<MarketDataPipeline> logger)
            : this(
                adapter,
                eventBus,
                dbFactory,
                new MarketDataValidator(Microsoft.Extensions.Options.Options.Create(new TradingSettings()), new SystemClock()),
                null,
                logger)
        {
        }

        public MarketDataPipeline(IIbkrAdapter adapter,
            ITradingEventBus eventBus,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IMarketDataValidator validator,
            ILogger<MarketDataPipeline> logger)
            : this(adapter, eventBus, dbFactory, validator, null, logger)
        {
        }

        public MarketDataPipeline(IIbkrAdapter adapter,
            ITradingEventBus eventBus,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IMarketDataValidator validator,
            IExitManagementService? exitManagementService,
            ILogger<MarketDataPipeline> logger,
            ITradingPipelineStatusService? pipelineStatusService = null)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _exitManagementService = exitManagementService;
            _pipelineStatusService = pipelineStatusService;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _adapter.MarketBarReceived += OnMarketBarReceived;
        }

        private async Task OnMarketBarReceived(MarketBar bar)
        {
            await ProcessMarketBarAsync(bar).ConfigureAwait(false);
        }

        public async Task ProcessMarketBarAsync(
            MarketBar bar,
            CancellationToken cancellationToken = default,
            bool allowStaleSeedCandle = false,
            bool publishToEventBus = true)
        {
            try
            {
                if (publishToEventBus)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.ReceivingCandle,
                        TradingPipelineActivityState.Active,
                        $"Received {bar.Symbol} {bar.Timeframe} candle.",
                        bar.Symbol);
                }

                if (!_validator.TryValidate(bar, out var validationReason))
                {
                    if (!allowStaleSeedCandle || !validationReason.Equals("Candle timestamp is stale.", StringComparison.OrdinalIgnoreCase))
                    {
                        if (publishToEventBus)
                        {
                            _pipelineStatusService?.Mark(
                                TradingPipelineStage.ReceivingCandle,
                                TradingPipelineActivityState.Error,
                                $"Rejected market data: {validationReason}",
                                bar.Symbol);
                        }

                        _logger.LogWarning("Rejected invalid market bar for {Symbol}: {Reason}", bar.Symbol, validationReason);
                        return;
                    }

                    _logger.LogDebug("Accepted stale historical seed candle for {Symbol} {Timeframe} {TimestampUtc}", bar.Symbol, bar.Timeframe, bar.TimestampUtc);
                }

                // Normalize to domain Candle
                var timeframe = bar.Timeframe switch
                {
                    "1m" => Timeframe.OneMinute,
                    "5m" => Timeframe.FiveMinutes,
                    "15m" => Timeframe.FifteenMinutes,
                    _ => Timeframe.OneMinute
                };

                var candle = new Candle(bar.Symbol, timeframe, bar.TimestampUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

                var persisted = await PersistCandleAsync(bar, timeframe, cancellationToken).ConfigureAwait(false);
                if (!persisted)
                {
                    if (publishToEventBus)
                    {
                        _pipelineStatusService?.Mark(
                            TradingPipelineStage.ReceivingCandle,
                            TradingPipelineActivityState.Error,
                            "Candle could not be persisted; analysis skipped.",
                            bar.Symbol);
                    }

                    return;
                }

                if (_exitManagementService != null && publishToEventBus)
                {
                    await _exitManagementService.ProcessMarketCandleAsync(candle, cancellationToken).ConfigureAwait(false);
                }

                if (publishToEventBus)
                {
                    var published = _eventBus.TryPublish(candle);
                    if (!published)
                    {
                        _logger.LogWarning("Failed to publish candle for {Symbol} {Timeframe} {Timestamp}", bar.Symbol, bar.Timeframe, bar.TimestampUtc);
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Persisted historical seed candle without publishing trade pipeline event for {Symbol} {Timeframe} {Timestamp}",
                        bar.Symbol,
                        bar.Timeframe,
                        bar.TimestampUtc);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (publishToEventBus)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.ReceivingCandle,
                        TradingPipelineActivityState.Error,
                        "Market data processing failed.",
                        bar.Symbol);
                }

                _logger.LogError(ex, "Error processing market bar");
            }
        }

        private async Task<bool> PersistCandleAsync(
            MarketBar bar,
            Timeframe timeframe,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var exists = await ctx.Candles.AnyAsync(
                    c => c.Symbol == bar.Symbol
                        && c.Timeframe == timeframe
                        && c.TimestampUtc == bar.TimestampUtc,
                    cancellationToken).ConfigureAwait(false);

                if (exists)
                {
                    _logger.LogInformation("Duplicate candle detected for {Symbol} {Timeframe} {Timestamp}", bar.Symbol, bar.Timeframe, bar.TimestampUtc);
                    return true;
                }

                ctx.Candles.Add(new TradingBot.Persistence.Candle
                {
                    Symbol = bar.Symbol,
                    Timeframe = timeframe,
                    TimestampUtc = bar.TimestampUtc,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume
                });
                await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogWarning(dbEx, "Failed to persist candle for {Symbol} {Timeframe} {Timestamp}", bar.Symbol, bar.Timeframe, bar.TimestampUtc);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to persist candle for {Symbol} {Timeframe} {Timestamp}", bar.Symbol, bar.Timeframe, bar.TimestampUtc);
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _adapter.MarketBarReceived -= OnMarketBarReceived;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to detach market bar event handler during disposal.");
            }
        }
    }
}
