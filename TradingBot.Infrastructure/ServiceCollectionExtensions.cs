using Microsoft.Extensions.DependencyInjection;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Services;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingBot.Infrastructure.Interfaces;
using System.Net.Http;
using TradingBot.Application.Configuration;

namespace TradingBot.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services)
        {
            services.AddSingleton<IClock, SystemClock>();

#if IBKR_API_AVAILABLE
            services.TryAddSingleton<IbkrBrokerService>();
            services.TryAddSingleton<IIbkrAdapter>(sp => sp.GetRequiredService<IbkrBrokerService>());
            services.TryAddSingleton<IOrderExecutionService>(sp => sp.GetRequiredService<IbkrBrokerService>());
            services.TryAddSingleton<IOrderModificationService>(sp => sp.GetRequiredService<IbkrBrokerService>());
            services.TryAddSingleton<IAccountService>(sp => sp.GetRequiredService<IbkrBrokerService>());
            services.TryAddSingleton<IPositionService>(sp => sp.GetRequiredService<IbkrBrokerService>());
            services.TryAddSingleton<IMarketDataService>(sp => sp.GetRequiredService<IbkrBrokerService>());
            services.TryAddSingleton<IIbkrConnectionService, IbkrConnectionService>();
#else
            var hasConcreteIbkrAdapter = services.Any(descriptor => descriptor.ServiceType == typeof(IIbkrAdapter));
            if (!hasConcreteIbkrAdapter)
            {
                services.TryAddSingleton<IIbkrAdapter, UnavailableIbkrAdapter>();
                services.TryAddSingleton<IIbkrConnectionService, UnavailableIbkrConnectionService>();
            }
            else
            {
                services.TryAddSingleton<IIbkrConnectionService, IbkrConnectionService>();
            }

            services.TryAddSingleton<IOrderExecutionService, OrderExecutionService>();
            services.TryAddSingleton<IAccountService, UnavailableAccountService>();
            services.TryAddSingleton<IPositionService, UnavailablePositionService>();
            services.TryAddSingleton<IMarketDataService, MarketDataService>();
#endif
            services.AddSingleton<ITradingEngineStatusService, TradingEngineStatusService>();
            services.AddSingleton<IOperatingModeService, OperatingModeService>();
            services.AddSingleton<ITradingPipelineStatusService, TradingPipelineStatusService>();
            services.AddSingleton<IInstrumentRegistryService, InstrumentRegistryService>();
            services.AddSingleton<IHistoricalBackfillService, HistoricalBackfillService>();
            services.AddTransient<IDashboardService, DashboardService>();
            services.AddTransient<ITradeDashboardService, TradeDashboardService>();
            services.AddTransient<IPatternAnalysisDashboardService, PatternAnalysisDashboardService>();
            services.AddTransient<IPostTradeAnalysisService, PostTradeAnalysisService>();
            services.AddTransient<ITradingControlService, TradingControlService>();
            services.AddSingleton<ITradePipelineChannel, TradePipelineChannel>();
            services.AddTransient<IBrokerStateReconciliationService, BrokerStateReconciliationService>();
            services.AddSingleton<ITradingExecutionGuard, TradingExecutionGuard>();
            services.AddSingleton<IPatternDetectionService, PatternDetectionService>();
            services.AddTransient<IStrategyEngine, StrategyEngine>();
            services.AddSingleton<IRiskManagementService, RiskManagementService>();
            services.AddTransient<IPositionSizer, PositionSizer>();
            services.AddTransient<IRiskEngine, RiskEngine>();
            services.AddTransient<IOrderManager, OrderManager>();
            services.AddSingleton<IExitManagementService, ExitManagementService>();
            services.AddTransient<IFeatureEngine, FeatureEngine>();
            services.AddSingleton<IPatternDetectorFactory, PatternDetectorFactory>();
            services.AddSingleton<IDeterministicReplayService, DeterministicReplayService>();
            services.AddSingleton<IAiAnalysisValidator, AiAnalysisValidator>();
            services.AddTransient<IAiUsageLimiter, AiUsageLimiter>();
            services.AddSingleton<IOpenAiApiKeyProvider, OpenAiApiKeyProvider>();
            services.AddTransient<IOpenAiSmokeTestService, OpenAiSmokeTestService>();
            services.AddSingleton<IMarketDataValidator, MarketDataValidator>();
            services.AddSingleton<IMarketDataQualityService, MarketDataQualityService>();
            services.AddSingleton<ILatestMarketDataService, LatestMarketDataService>();
            services.AddSingleton<IMarketDataCollectionStatusService, MarketDataCollectionStatusService>();
            services.AddSingleton<IMarketDatasetStore, ParquetMarketDatasetStore>();
            services.AddSingleton<MarketDatasetWriterHostedService>();
            services.AddSingleton<IMarketDatasetSink>(sp => sp.GetRequiredService<MarketDatasetWriterHostedService>());
            services.AddSingleton<IPatternQualityGate, PatternQualityGate>();
            services.AddTransient<IProtectiveStopMonitor, ProtectiveStopMonitor>();
            services.AddTransient<IPatternDetector>(sp => sp.GetRequiredService<IPatternDetectorFactory>().Create());
            services.AddTransient<IMarketSnapshotService, MarketSnapshotService>();
            services.AddSingleton<HttpClient>();
            services.AddTransient<IAiMarketAnalyzer, OpenAiMarketAnalyzer>();
            services.AddTransient<IAiTradeCritic, OpenAiTradeCritic>();

            services.AddHostedService<InstrumentRegistryHostedService>();
            services.AddHostedService(sp => sp.GetRequiredService<MarketDatasetWriterHostedService>());
            services.AddHostedService<HistoricalBackfillHostedService>();
            services.AddHostedService<DeterministicReplayHostedService>();
            services.AddHostedService<BrokerStateReconciliationHostedService>();
            services.AddHostedService<RuntimeBrokerReconciliationHostedService>();
            services.AddHostedService<ProtectiveStopInvariantHostedService>();
            services.AddHostedService<ExitManagementHostedService>();
            services.AddHostedService<MarketDataSubscriptionHostedService>();
            services.AddHostedService<CandlePatternDetectionBackgroundService>();
            services.AddHostedService<PatternDecisionBackgroundService>();
            services.AddHostedService<ApprovedOrderExecutionBackgroundService>();

            // register trading event bus with default options
            services.AddSingleton(sp => new TradingEventBusOptions());
            services.AddSingleton<ITradingEventBus, TradingEventBus>(sp =>
            {
                var opts = sp.GetRequiredService<TradingEventBusOptions>();
                var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TradingEventBus>>();
                return new TradingEventBus(opts, logger);
            });

            // IBKR adapter boundary: register default options only.
            // A concrete IIbkrAdapter implementation that references the official IB API
            // should be registered by the host application. If a concrete IIbkrAdapter
            // is registered, callers can construct an IbkrConnectionService using that adapter.
            services.AddSingleton(sp =>
            {
                var ibkr = sp.GetRequiredService<IOptions<IbkrSettings>>().Value;
                var trading = sp.GetRequiredService<IOptions<TradingSettings>>().Value;
                return new IbkrOptions
                {
                    Host = ibkr.Host,
                    Port = ibkr.GetPort(trading.OperatingMode),
                    ClientId = ibkr.ClientId,
                    AccountId = trading.OperatingMode == TradingBot.Domain.Enums.TradingOperatingMode.PaperTrading
                        ? ibkr.PaperAccountId
                        : ibkr.AccountId
                };
            });
            services.AddSingleton<MarketDataPipeline>();
            services.AddSingleton<ICandleHistoryService, CandleHistoryService>();

            return services;
        }
    }
}
