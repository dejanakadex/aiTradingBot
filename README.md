# TrdBot

[![CI](https://github.com/dejanakadex/aiTradingBot/actions/workflows/ci.yml/badge.svg?branch=trading-bot-v2)](https://github.com/dejanakadex/aiTradingBot/actions/workflows/ci.yml)

AI-assisted trading bot (layered .NET 10 LTS solution).

The solution includes an event-driven analysis/strategy/risk pipeline, conditional IBKR
integration, exit management, EF Core persistence (SQLite), and a Blazor dashboard.

## Review and development plan

Ongoing work uses one branch: `trading-bot-v2`.

- [Detailed project review](docs/PROJECT_REVIEW.md) — code findings, evidence, test gaps and verification limits (Croatian).
- [Proposed v2 plan](docs/V2_PLAN.md) — implementation phases and acceptance criteria, pending confirmation (Croatian).

The review found gaps in daily-risk inputs, broker/exit lifecycle handling and data freshness.
Existing safety components should be read together with those findings. The review/cleanup
commit changes repository hygiene and documentation only. The current .NET 10 CI build is
verified on GitHub Actions; see the workflow badge and Continuous integration section below.

## Quick start

Prerequisites
- .NET 10 SDK: https://dotnet.microsoft.com

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

## Continuous integration

GitHub Actions runs restore, Release build and the complete test project on pushes and pull
requests targeting `main` or `trading-bot-v2`. Test results and coverage output are retained
as workflow artifacts for 14 days. The workflow uses `global.json`, so local development and
CI resolve the same .NET 10 SDK feature band policy.

Dependabot checks NuGet packages weekly and GitHub Actions monthly, opening update pull
requests against `trading-bot-v2`.

The standard CI build exercises the broker-unavailable compilation path because the official
IBKR `CSharpAPI.dll` is not stored in the repository. Use the explicit `IbkrApiDll` build shown
below to compile and test the real adapter in an environment where the official DLL is installed.

## Configuration

- Database connection string: set in `TradingBot.Web/appsettings.json` or provide the environment variable `ConnectionStrings__TradingBot`.
- The persistence registration creates the configured SQLite directory; startup applies migrations.
- Supply account-specific configuration and API keys through environment variables or your local secret configuration. Keep shared settings free of credentials.
- `appsettings.Local.json` patterns are ignored for local use, but the host does not automatically load those files; use an explicitly configured provider or environment variables.

### IBKR build prerequisite

The real adapter is compiled only when the official `CSharpAPI.dll` exists. The default
`IbkrApiDll` path is `C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\CSharpAPI.dll`.
An explicit path can be supplied when building/testing:

```powershell
dotnet build TradingBot.sln -p:IbkrApiDll="C:\path\to\CSharpAPI.dll"
dotnet test TradingBot.sln -p:IbkrApiDll="C:\path\to\CSharpAPI.dll"
```

Without that DLL, the unavailable/fallback broker services are used. Passing tests in that
configuration does not verify the real adapter. Generated copies of broker DLLs in `bin`
are not a substitute for this prerequisite.

### Repository hygiene

`bin`, `obj`, `.vs`, `.verify-bin`, test/coverage output, logs and runtime SQLite databases
are ignored. Restore/build regenerates build output; migrations initialize a new database.
Source files, migrations, shared settings and web assets remain tracked.

Before switching an existing checkout to the cleanup branch, stop the app and back up your
local `TradingBot.Web/Data/trading.db` and its SQLite sidecars together. Those files were
previously tracked, so switching branches can remove them. The cleanup removes them from
the current Git tree and does not erase earlier commits.

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
- `FeatureEngine` and `PatternDetector` implemented; unit tests exist. See the review for current verification limits.
- `MarketSnapshotService` builds structured AI-ready market context across 1m, 5m and 15m candles without making trade decisions.
- `OpenAiMarketAnalyzer` evaluates structured market snapshots with schema-constrained AI output and persists analysis records; unavailable or invalid AI results default to `REJECT` / no trade.
- `OpenAiTradeCritic` reviews proposed AI trade analysis for rejection reasons and persists critic results; AI errors, timeouts or malformed responses default to rejected.
- `StrategyEngine` applies configurable strategy rules, calculates entry/stop/target/reward-risk and persists every approved or rejected setup; it does not submit orders or calculate final account position size.
- `RiskEngine` and `PositionSizer` implement deterministic account, exposure and loss checks. Production history wiring and the zero-capacity sizing case still need correction (R01–R02).
- `OrderManager` implements approved-order submission, broker status/fill persistence and duplicate checks. Crash recovery, real partial-fill handling and exit coordination have open findings (R03–R07).
- Broker-state reconciliation gates startup readiness: trading remains disabled until IBKR positions/open orders are compared with SQLite open trades/orders and reconciled.
- Market data subscription startup waits for the engine to become `Ready`, seeds configured historical candles and subscribes to every enabled `TradingSettings.Instruments` entry using its configured timeframes. The checked-in fail-closed example contains SPY on `1m`, `5m` and `15m`; legacy `Symbols` remains a temporary compatibility fallback.
- Channel-based background services connect the pipeline without a giant trading loop: candle pattern detection, pattern decisioning and approved order execution run as focused async consumers.
- The Blazor dashboard shows engine/broker/market/AI/risk/P&L status with realtime refresh and control actions for pause, resume, close-current-position request and kill switch.
- Blazor analytics pages show recent trades and pattern performance with filters for symbol, pattern, date range, market regime, VWAP context, 15m trend and AI decisions.
- Paper trading safety mode adds explicit operating modes: `AnalysisOnly`, `PaperTrading` and `LiveTrading`. The default is `AnalysisOnly`, and live trading requires explicit configuration.
- Post-trade analysis storage and query services exist, including P/L, fees, MFE/MAE and holding duration. Automatic invocation from the completed-trade lifecycle is still missing (R11).
- Dependency health checks, readiness endpoints, startup validation, shutdown logging and bounded channels are implemented. The review identifies remaining gaps in fail-closed behavior and operational controls.
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
- Market data startup: `TradingSettings.Instruments` defines stable instrument IDs, broker metadata, allowed directions, strategy IDs, per-instrument timeframes and optional limits. `MarketDataSubscriptionHostedService` seeds and subscribes every enabled instrument. `TradingEnabled` defaults to false and will be joined with persisted readiness in onboarding point 2; global `AnalysisOnly` remains active.
- Pipeline identity: every pattern has a deterministic `SignalId` plus a per-run `CorrelationId`, `InstrumentId`, `StrategyId` and explicit market-data/feature/pattern/strategy contract versions. The same context is propagated into strategy and risk audit JSON.
- Pattern detection: deterministic rules implemented for Hammer, Bullish Engulfing, Double Bottom, BreakoutAndRetest, VWAP Reclaim. Options are in `PatternDetectorOptions`.
- Market snapshots: `IMarketSnapshotService` combines 15m broader direction, 5m setup/pullback context and 1m entry timing into a structured `MarketSnapshot` for later AI analysis. It includes candles, features, patterns, support/resistance candidates, trend, volume, volatility, current price when available and spread when supplied.
- AI analysis: `IAiMarketAnalyzer` is analysis-only. It sends structured snapshot data to OpenAI using JSON schema response format, validates the response, logs duration/token usage when available, persists the analysis to SQLite and defaults to no trade (`REJECT`) on unavailable or invalid AI output.
- AI trade criticism: `IAiTradeCritic` is rejection-focused. It reviews the snapshot, detected pattern and AI analysis for risks such as higher-timeframe weakness, sell volume, false breakout risk, poor reward/risk, excessive volatility, poor liquidity, large spread, timeframe conflicts and weak pattern structure. It never submits orders and defaults to rejected when unavailable or invalid.
- AI cost controls: `PatternQualityGate` uses `PatternDetectorOptions.MinimumTradeSetupQuality` and per-pattern thresholds to decide whether a candidate reaches OpenAI. `TradingSettings.MinimumPatternQualityForAiAnalysis` remains in settings/telemetry but is not the active gate. The critic runs only for an actionable, sufficiently confident BUY; WAIT, REJECT and safe fallbacks skip it.
- AI context sizing: `OpenAiSettings.MarketContext` controls rich snapshot candle history sent to AI. Defaults are 60 one-minute candles, 50 five-minute candles and 40 fifteen-minute candles while preserving OHLCV, indicators, volume, volatility, VWAP, support/resistance, trend and pattern context.
- AI usage telemetry: analyzer/critic calls store token, model, prompt-version, duration and success/failure records. Retries currently share one record per completed service call rather than one per HTTP attempt (R15). Dashboard pricing is configurable and is not an authoritative bill.
- Strategy engine: `IStrategyEngine` receives the snapshot, pattern, AI analysis and critic result. It creates a `TradeSignal` only when configured rules pass for AI confidence, pattern quality, critic approval, reward/risk, spread, market regime and trading hours. It persists all decisions, including rejected setups and reasons, and leaves final position sizing to later risk management.
- Risk engine: `IRiskEngine` accepts strategy, account, positions, completed trades and open orders; `IPositionSizer` calculates quantity from risk and exposure limits. The production caller currently supplies an empty completed-trade list, and zero leverage capacity is filtered out by the sizer (R01–R02).
- Order management: `IOrderManager` supports approved limit buys, parent-linked bracket orders, cancellation, status and execution persistence. The conditional broker adapter and lifecycle require the partial-fill, cancellation, metadata and idempotency corrections in R03–R07.
- Broker reconciliation: startup state moves through `Starting`, `Connecting`, `Reconciling`, then `Ready` only when broker positions/open orders match SQLite open trades/orders. Mismatches such as broker-only SPY shares or local-only open orders keep the engine `Degraded` or `Faulted` with trading disabled. The web app exposes `GET /api/reconciliation/status` for readiness/status monitoring.
- Background pipeline: hosted services consume channels with cancellation propagation and per-message exception isolation. `CandlePatternDetectionBackgroundService` consumes candles and publishes patterns. `PatternDecisionBackgroundService` gates on engine `Ready` plus `TradingSettings.Enabled`, builds snapshots and runs AI/critic/strategy/risk before publishing approved plans. `ApprovedOrderExecutionBackgroundService` consumes approved plans and calls `IOrderManager`, which uses the centralized execution guard before any broker submission.
- Dashboard: the Blazor home page shows engine, broker, market, AI, risk and position status. Controls call `ITradingControlService` and persist audit records. Close Current Position currently pauses and records a request without submitting an exit; reconnect can override a manual pause/kill state (R08).
- Trade analytics: `/trades` lists recent trades with timestamp, symbol, pattern, AI confidence, entry/exit, quantity, gross P/L, fees, net P/L and result. `/patterns` shows aggregate pattern statistics plus decision audit rows linking pattern candidates, AI/critic/strategy/risk decisions and actual outcomes. Read services use bounded EF Core projections/grouping rather than loading whole tables for statistics.
- Operating modes: `AnalysisOnly` stores hypothetical trades instead of submitting broker orders. `PaperTrading` requires a broker environment verified as paper; `LiveTrading` additionally requires `LiveTradingExplicitlyEnabled`. Runtime mode/account transitions need the lifecycle corrections described in R08.
- Post-trade analysis: `IPostTradeAnalysisService` can persist complete context and calculate P/L, fees, MFE/MAE and holding duration. Its store method has no production caller yet (R11); read-only analysis does not automatically change strategy configuration.
- Reliability: health checks separate process liveness, database connectivity, broker connection, trading readiness and OpenAI configuration. Startup validates options. Existing fail-closed and idempotency mechanisms have the integration gaps documented in the review; they are not a completed verification of trading safety.

What to give Codex for fastest, highest-quality results

- Clear mission statement (one sentence): e.g. "Implement concrete IB/TWS adapter using official IBApi and publish normalized Candle to `ITradingEventBus`."
- Exact files to modify (from the list above).
- Example runtime data: a short CSV or JSON of `MarketBar` messages (timestamped) to validate normalization and persistence.
- Any organization policy or package restrictions (allowed nuget packages, offline-only, etc.).
- Desired acceptance criteria: unit tests to add or pass, performance bounds, or behavioral checks (dedupe behavior, publish latency).

Suggested next tasks

- Confirm the [v2 plan](docs/V2_PLAN.md).
- Establish a reproducible build/test baseline, including the conditional real IBKR adapter.
- Fix risk/lifecycle findings and add end-to-end callback/restart scenarios.
- Validate timestamps, quotes and replay before extending data collection and model evaluation.

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
