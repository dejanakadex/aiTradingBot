# TrdBot

AI-assisted trading bot starter repository (layered .NET 9 solution).

This repo provides a scaffold and several implemented components to accelerate development
of a trading engine: domain models, application interfaces, an event-driven pipeline,
EF Core persistence (SQLite), a feature engine and a deterministic pattern detector.

## Quick start

Prerequisites
- .NET 9 SDK: https://dotnet.microsoft.com

Restore, build and run the Blazor server frontend:

```powershell
dotnet restore
dotnet build
dotnet run --project TradingBot.Web
```

Health endpoint:

```
GET /api/health
```

Operational health endpoints:

```
GET /api/health/live
GET /api/health/details
GET /api/health/ready
GET /api/health/trading-ready
GET /api/reconciliation/status
```

`/api/health` and `/api/health/live` report web-process health. Dependency and trading readiness endpoints report database, IBKR, trading engine and OpenAI status separately, so the web app can remain healthy while trading is unavailable.

Run unit tests:

```powershell
dotnet test
```

Run a single test project:

```powershell
dotnet test TradingBot.Tests/TradingBot.Tests.csproj
```

## Configuration

- Database connection string: set in `TradingBot.Web/appsettings.json` or provide an environment variable `ConnectionStrings:TradingBot`.
- Ensure the configured SQLite data directory exists if using a file path.

## Project layout (key folders)

- `TradingBot.Web` — Blazor UI and host `Program.cs`.
- `TradingBot.Application` — DTOs and application interfaces (e.g., `ITradingEventBus`, `IPatternDetector`).
- `TradingBot.Domain` — domain models and enums (e.g., `Candle`, `PatternCandidate`).
- `TradingBot.Infrastructure` — concrete services: bounded channel event bus, market data pipeline, feature engine, pattern detector, IBKR adapter boundary and connection service.
- `TradingBot.Persistence` — EF Core `TradingBotDbContext`, entities and migrations; SQLite by default.
- `TradingBot.Tests` — xUnit tests (domain, feature engine, event bus, market pipeline, pattern detector).

## Current status (brief)

- Solution scaffolded with layered projects and DI.
- Logging wired with Microsoft Logging + NLog config (`TradingBot.Web/nlog.config`).
- Persistence implemented with EF Core + SQLite; migrations present.
- Eventing via bounded `System.Threading.Channels` (`TradingEventBus`).
- `FeatureEngine` and `PatternDetector` implemented; unit tests exist and pass locally.
- `MarketSnapshotService` builds structured AI-ready market context across 1m, 5m and 15m candles without making trade decisions.
- `OpenAiMarketAnalyzer` evaluates structured market snapshots with schema-constrained AI output and persists analysis records; unavailable or invalid AI results default to `REJECT` / no trade.
- `OpenAiTradeCritic` reviews proposed AI trade analysis for rejection reasons and persists critic results; AI errors, timeouts or malformed responses default to rejected.
- `StrategyEngine` applies configurable strategy rules, calculates entry/stop/target/reward-risk and persists every approved or rejected setup; it does not submit orders or calculate final account position size.
- `RiskEngine` and `PositionSizer` deterministically enforce account, exposure and loss limits before any order can be considered; uncertainty defaults to rejected and every risk decision is persisted.
- `OrderManager` submits only already-approved orders through the execution abstraction, tracks broker status/fills, persists broker order IDs and executions, and prevents duplicate submissions.
- Broker-state reconciliation gates startup readiness: trading remains disabled until IBKR positions/open orders are compared with SQLite open trades/orders and reconciled.
- Market data subscription startup waits for the engine to become `Ready`, seeds configured historical candles and subscribes to configured symbols/timeframes for live candle flow. Defaults are `SPY` on `1m`, `5m` and `15m`.
- Channel-based background services connect the pipeline without a giant trading loop: candle pattern detection, pattern decisioning and approved order execution run as focused async consumers.
- The Blazor dashboard shows engine/broker/market/AI/risk/P&L status with realtime refresh and control actions for pause, resume, close-current-position request and kill switch.
- Blazor analytics pages show recent trades and pattern performance with filters for symbol, pattern, date range, market regime, VWAP context, 15m trend and AI decisions.
- Paper trading safety mode adds explicit operating modes: `AnalysisOnly`, `PaperTrading` and `LiveTrading`. The default is `AnalysisOnly`, and live trading requires explicit configuration.
- Post-trade analysis storage records the complete decision context for completed trades, calculates P/L, fees, MFE/MAE and holding duration, and exposes read-only performance/losing-condition analysis services. Recommendations require manual approval and never change production strategy configuration automatically.
- Operational reliability pass added dependency health checks, readiness endpoints, startup configuration validation, graceful shutdown logging, visible database/IBKR/OpenAI failure paths, bounded approved-plan channel overflow handling and stronger concurrent duplicate-order protection. Trading continues to fail closed when dependencies are unavailable or uncertain.
- AI usage optimization gates OpenAI calls with deterministic pattern quality before analysis and calls the AI critic only for valid high-confidence BUY analyzer results. Skipped calls are persisted as pipeline decision records, and OpenAI token usage is stored in SQLite for dashboard statistics.
- `IIbkrAdapter` boundary, `IbkrConnectionService` and a conditional concrete IB/TWS adapter are implemented. The real adapter is compiled when the official TWS C# API DLL is installed at the configured path.

## Developer summary for Codex

Purpose: continue implementing features, adapters, or tests. Use the following targeted files as entry points.

High-priority files to open for Codex:

- `TradingBot.Web/Program.cs`
- `TradingBot.Infrastructure/ServiceCollectionExtensions.cs`
- `TradingBot.Persistence/TradingBotDbContext.cs`
- `TradingBot.Infrastructure/Services/TradingEventBus.cs`
- `TradingBot.Infrastructure/Services/MarketDataPipeline.cs`
- `TradingBot.Infrastructure/Services/PatternDetector.cs`
- `TradingBot.Infrastructure/Services/MarketSnapshotService.cs`
- `TradingBot.Infrastructure/Services/OpenAiMarketAnalyzer.cs`
- `TradingBot.Infrastructure/Services/OpenAiTradeCritic.cs`
- `TradingBot.Infrastructure/Services/StrategyEngine.cs`
- `TradingBot.Infrastructure/Services/RiskEngine.cs`
- `TradingBot.Infrastructure/Services/PositionSizer.cs`
- `TradingBot.Infrastructure/Services/OrderManager.cs`
- `TradingBot.Infrastructure/Services/BrokerStateReconciliationService.cs`
- `TradingBot.Infrastructure/Services/TradingEngineStatusService.cs`
- `TradingBot.Infrastructure/Services/DashboardService.cs`
- `TradingBot.Infrastructure/Services/TradeDashboardService.cs`
- `TradingBot.Infrastructure/Services/PatternAnalysisDashboardService.cs`
- `TradingBot.Infrastructure/Services/PostTradeAnalysisService.cs`
- `TradingBot.Web/HealthChecks/DatabaseHealthCheck.cs`
- `TradingBot.Web/HealthChecks/IbkrConnectionHealthCheck.cs`
- `TradingBot.Web/HealthChecks/OpenAiAvailabilityHealthCheck.cs`
- `TradingBot.Web/HealthChecks/TradingEngineReadinessHealthCheck.cs`
- `TradingBot.Infrastructure/Services/TradingControlService.cs`
- `TradingBot.Infrastructure/Services/OperatingModeService.cs`
- `TradingBot.Infrastructure/Background/CandlePatternDetectionBackgroundService.cs`
- `TradingBot.Infrastructure/Background/MarketDataSubscriptionHostedService.cs`
- `TradingBot.Infrastructure/Background/PatternDecisionBackgroundService.cs`
- `TradingBot.Infrastructure/Background/ApprovedOrderExecutionBackgroundService.cs`
- `TradingBot.Infrastructure/Options/PatternDetectorOptions.cs`
- `TradingBot.Domain/Models/Candle.cs`
- `TradingBot.Domain/Models/MarketSnapshot.cs`
- `TradingBot.Domain/Models/AiMarketAnalysisResult.cs`
- `TradingBot.Domain/Models/AiTradeCriticResult.cs`
- `TradingBot.Domain/Models/StrategyDecision.cs`
- `TradingBot.Domain/Models/PositionSizingResult.cs`
- `TradingBot.Domain/Models/ManagedOrderResult.cs`
- `TradingBot.Domain/Models/BrokerReconciliationStatus.cs`
- `TradingBot.Domain/Models/TradePipelineMessages.cs`
- `TradingBot.Domain/Models/DashboardSnapshot.cs`
- `TradingBot.Domain/Models/DashboardAnalyticsModels.cs`
- `TradingBot.Domain/Models/PostTradeAnalysisModels.cs`
- `TradingBot.Domain/Enums/TradingOperatingMode.cs`
- `TradingBot.Application/Interfaces/IPatternDetector.cs`
- `TradingBot.Application/Interfaces/IMarketSnapshotService.cs`
- `TradingBot.Application/Interfaces/IAiMarketAnalyzer.cs`
- `TradingBot.Application/Interfaces/IAiTradeCritic.cs`
- `TradingBot.Application/Interfaces/IStrategyEngine.cs`
- `TradingBot.Application/Interfaces/IRiskEngine.cs`
- `TradingBot.Application/Interfaces/IPositionSizer.cs`
- `TradingBot.Application/Interfaces/IOrderManager.cs`
- `TradingBot.Application/Interfaces/IBrokerStateReconciliationService.cs`
- `TradingBot.Application/Interfaces/ITradingEngineStatusService.cs`
- `TradingBot.Application/Interfaces/ITradePipelineChannel.cs`
- `TradingBot.Application/Interfaces/IDashboardService.cs`
- `TradingBot.Application/Interfaces/ITradeDashboardService.cs`
- `TradingBot.Application/Interfaces/IPatternAnalysisDashboardService.cs`
- `TradingBot.Application/Interfaces/IPostTradeAnalysisService.cs`
- `TradingBot.Application/Interfaces/ITradingControlService.cs`
- `TradingBot.Application/Interfaces/IOperatingModeService.cs`
- `TradingBot.Tests/PatternDetectorTests.cs`
- `TradingBot.Tests/MarketSnapshotServiceTests.cs`
- `TradingBot.Tests/OpenAiMarketAnalyzerTests.cs`
- `TradingBot.Tests/OpenAiTradeCriticTests.cs`
- `TradingBot.Tests/StrategyEngineTests.cs`
- `TradingBot.Tests/RiskEngineTests.cs`
- `TradingBot.Tests/OrderManagerTests.cs`
- `TradingBot.Tests/BrokerStateReconciliationTests.cs`
- `TradingBot.Tests/BackgroundPipelineTests.cs`
- `TradingBot.Tests/DashboardAnalyticsServiceTests.cs`
- `TradingBot.Tests/PostTradeAnalysisServiceTests.cs`
- `TradingBot.Tests/AiPipelineOptimizationTests.cs`

Short technical notes for Codex

- EF Core: uses `IDbContextFactory<TradingBotDbContext>` and SQLite. Candle entity is uniquely indexed on `(Symbol, Timeframe, TimestampUtc)` to prevent duplicates.
- Event bus: `TradingEventBus` uses bounded `Channel<T>` with configurable full-mode strategies (Wait, DropOldest, DropNewest, Reject). Keep `TryPublish` non-blocking for IB callbacks.
- IBKR: `IIbkrAdapter` is the broker boundary. The conditional concrete adapter uses the official TWS C# API when available, and publishes normalized `MarketBar` DTOs through market-data subscriptions.
- Market data startup: `TradingSettings.Symbols` and `TradingSettings.MarketDataTimeframes` control automatic subscriptions. `MarketDataSubscriptionHostedService` seeds historical candles and opens live subscriptions only after broker reconciliation marks the engine `Ready`.
- Pattern detection: deterministic rules implemented for Hammer, Bullish Engulfing, Double Bottom, BreakoutAndRetest, VWAP Reclaim. Options are in `PatternDetectorOptions`.
- Market snapshots: `IMarketSnapshotService` combines 15m broader direction, 5m setup/pullback context and 1m entry timing into a structured `MarketSnapshot` for later AI analysis. It includes candles, features, patterns, support/resistance candidates, trend, volume, volatility, current price when available and spread when supplied.
- AI analysis: `IAiMarketAnalyzer` is analysis-only. It sends structured snapshot data to OpenAI using JSON schema response format, validates the response, logs duration/token usage when available, persists the analysis to SQLite and defaults to no trade (`REJECT`) on unavailable or invalid AI output.
- AI trade criticism: `IAiTradeCritic` is rejection-focused. It reviews the snapshot, detected pattern and AI analysis for risks such as higher-timeframe weakness, sell volume, false breakout risk, poor reward/risk, excessive volatility, poor liquidity, large spread, timeframe conflicts and weak pattern structure. It never submits orders and defaults to rejected when unavailable or invalid.
- AI cost controls: `TradingSettings.MinimumPatternQualityForAiAnalysis` defaults to `0.6`; candidates below this deterministic `PatternCandidate.Confidence` threshold skip OpenAI and persist `PatternRejectedBeforeAi`. Candidates at or above threshold persist `PatternSentToAi` and call the market analyzer. The critic runs only when analyzer action is `BUY`, analyzer confidence is at least `TradingSettings.MinimumAiConfidence`, and the analyzer result is not a safe fallback. WAIT, REJECT, low-confidence BUY, timeout and invalid analyzer results stop before critic and persist `CriticSkipped`.
- AI context sizing: `OpenAiSettings.MarketContext` controls rich snapshot candle history sent to AI. Defaults are 60 one-minute candles, 50 five-minute candles and 40 fifteen-minute candles while preserving OHLCV, indicators, volume, volatility, VWAP, support/resistance, trend and pattern context.
- AI usage telemetry: each actual OpenAI API attempt records an `AiApiUsageRecord` with request type (`MarketAnalyzer` or `TradeCritic`), symbol, pattern, timestamp, input/output/total tokens, cached input tokens when present, duration, model and success/failure. The dashboard shows today's AI calls, analyzer/critic split and token totals. Pricing fields are configurable but default to zero and no model prices are hard-coded.
- Strategy engine: `IStrategyEngine` receives the snapshot, pattern, AI analysis and critic result. It creates a `TradeSignal` only when configured rules pass for AI confidence, pattern quality, critic approval, reward/risk, spread, market regime and trading hours. It persists all decisions, including rejected setups and reasons, and leaves final position sizing to later risk management.
- Risk engine: `IRiskEngine` receives the strategy decision, IBKR account info, current broker positions, today's completed trades and open orders. It is deterministic and cannot be overridden by AI. `IPositionSizer` sizes from maximum allowed risk divided by entry-stop distance, then enforces maximum position value, buying power and leverage. `IRiskEngine` also enforces trading enabled, daily loss, open position and consecutive-loss limits, persists every `RiskDecision` and defaults to `Reject` when inputs are unavailable.
- Order management: `IOrderManager` receives only already-approved `OrderRequest` objects and uses `IOrderExecutionService` as the broker execution abstraction. It supports limit buys, stop-loss/take-profit bracket-style workflows through separate broker-side submissions, cancel, status lookup and broker status/fill updates. It does not assume fills after submission, tracks partial fills, persists broker order IDs, average fill price and commissions when available, records executions and rejects safely on adapter disconnects.
- Broker reconciliation: startup state moves through `Starting`, `Connecting`, `Reconciling`, then `Ready` only when broker positions/open orders match SQLite open trades/orders. Mismatches such as broker-only SPY shares or local-only open orders keep the engine `Degraded` or `Faulted` with trading disabled. The web app exposes `GET /api/reconciliation/status` for readiness/status monitoring.
- Background pipeline: hosted services consume channels with cancellation propagation and per-message exception isolation. `CandlePatternDetectionBackgroundService` consumes candles and publishes patterns. `PatternDecisionBackgroundService` gates on engine `Ready` plus `TradingSettings.Enabled`, builds snapshots and runs AI/critic/strategy/risk before publishing approved plans. `ApprovedOrderExecutionBackgroundService` consumes approved plans and calls `IOrderManager`, which uses the centralized execution guard before any broker submission.
- Dashboard: the Blazor home page is a functional operations dashboard backed by `IDashboardService`. It shows application, IBKR and trading engine state, current market/position context, latest AI/critic info, daily trade statistics, exposure, leverage and daily loss versus limit. UI controls call `ITradingControlService`; destructive actions require browser confirmation, are logged and persisted to `BotSessions`, and never call IBKR directly.
- Trade analytics: `/trades` lists recent trades with timestamp, symbol, pattern, AI confidence, entry/exit, quantity, gross P/L, fees, net P/L and result. `/patterns` shows aggregate pattern statistics plus decision audit rows linking pattern candidates, AI/critic/strategy/risk decisions and actual outcomes. Read services use bounded EF Core projections/grouping rather than loading whole tables for statistics.
- Operating modes: startup logs the selected operating mode. `AnalysisOnly` runs the complete analysis pipeline but stores hypothetical trades instead of calling `IOrderManager`. `PaperTrading` allows broker submission only when `IbkrSettings.AccountId` matches `PaperAccountId`. `LiveTrading` is blocked unless `TradingSettings.LiveTradingExplicitlyEnabled` is true. Mode changes go through `IOperatingModeService`, are logged and persisted, and AI components have no access to change the mode.
- Post-trade analysis: `IPostTradeAnalysisService` persists completed-trade context into `PostTradeAnalysisRecords`, including `MarketSnapshot`, `PatternCandidate`, AI analysis, critic analysis, strategy/risk decisions, order data, execution data and calculated trade result metrics. Query methods answer pattern success questions such as Double Bottom overall, Double Bottom above VWAP and Hammer with bullish 15m trend through EF Core filters/grouping. Losing-condition summaries and improvement recommendations are analysis-only; they do not mutate configuration or strategy code.
- Reliability: health checks distinguish web liveness, SQLite connectivity, IBKR connection status, trading-engine readiness and OpenAI configuration availability. Startup validates option ranges and blocks accidental live mode unless explicitly enabled. Reconciliation exceptions mark the engine `Faulted` with trading disabled. OpenAI analysis/critic components return no-trade/reject on request, parsing or persistence uncertainty. Order submission uses idempotency keys plus keyed in-process locks to prevent concurrent duplicate submissions.

What to give Codex for fastest, highest-quality results

- Clear mission statement (one sentence): e.g. "Implement concrete IB/TWS adapter using official IBApi and publish normalized Candle to `ITradingEventBus`."
- Exact files to modify (from the list above).
- Example runtime data: a short CSV or JSON of `MarketBar` messages (timestamped) to validate normalization and persistence.
- Any organization policy or package restrictions (allowed nuget packages, offline-only, etc.).
- Desired acceptance criteria: unit tests to add or pass, performance bounds, or behavioral checks (dedupe behavior, publish latency).

Suggested next tasks

- Validate the real IBKR market-data subscription flow during market hours or with delayed data permissions enabled.
- Expand `PatternDetector` unit tests to cover negative and edge cases.
- Add integration tests that simulate streaming `MarketBar` data and assert persistence + event bus publication.
- Connect `MarketSnapshotService` and `IAiMarketAnalyzer` to the strategy/risk flow once the final decision contract is defined.

## Contact points in the code (where to modify)

- DI registration: `TradingBot.Infrastructure/ServiceCollectionExtensions.cs` — register or replace `IIbkrAdapter` implementation here.
- Market data entry: `TradingBot.Infrastructure/Services/MarketDataPipeline.cs` — consumes `MarketBar` from adapter, normalizes to `Candle`, publishes and persists.
- Persistence model: `TradingBot.Persistence/Entities/Candle.cs` and `TradingBot.Persistence/TradingBotDbContext.cs`.

## Helpful commands

```powershell
# Build and run web app
dotnet build
dotnet run --project TradingBot.Web

# Run all tests
dotnet test

# Run only pattern detector tests
dotnet test TradingBot.Tests/TradingBot.Tests.csproj --filter FullyQualifiedName~PatternDetectorTests
```

## License

See `LICENSE` in repository root.

---

If you'd like, I will commit this `README.md` update and push a branch; or I can also scaffold a concrete `IIbkrAdapter` once you add the official IBApi package. Tell me which you prefer.

