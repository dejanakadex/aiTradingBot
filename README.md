# TrdBot

[![CI](https://github.com/dejanakadex/aiTradingBot/actions/workflows/ci.yml/badge.svg?branch=trading-bot-v2)](https://github.com/dejanakadex/aiTradingBot/actions/workflows/ci.yml)

AI-assisted trading bot (layered .NET 10 LTS solution).

The solution includes an event-driven analysis/strategy/risk pipeline, required IBKR
integration, exit management, EF Core persistence (SQLite), and a Blazor dashboard.
The initial exit profile is a fixed BUY/stop/target bracket; the trailing profile remains selectable. A persisted deadline from the first fill is checked every second without market data. Paper order behavior still requires an actual IBKR paper end-to-end test before it is considered verified.
The local end-to-end suite drives onboarding through protected exit with SQLite and a deterministic fake broker, including stop, target, timer, partial fill, rejection, disconnect, restart and simultaneous multi-instrument reservation scenarios. It verifies durable order/allocation/audit state and released capacity after closure; it does not substitute for the IBKR paper test.

## Review and development plan

Ongoing work uses one branch: `trading-bot-v2`.

- [Detailed project review](docs/PROJECT_REVIEW.md) — code findings, evidence, test gaps and verification limits (Croatian).
- [Proposed v2 plan](docs/V2_PLAN.md) — implementation phases and acceptance criteria, pending confirmation (Croatian).
- [Additive EUR/USD plan](docs/EURUSD_READINESS.md) — FX-1 through FX-12, preserving stock support (Croatian).
- [Stock compatibility baseline](docs/STOCK_COMPATIBILITY_BASELINE.md) — existing features, FX gaps and fixed regression expectations (Croatian).

The review tracks both original findings and their implementation status. Daily-risk inputs,
signal arbitration, the durable broker/exit lifecycle, the final execution-grade scalping gate
and measured per-instrument shadow/paper rollout are now addressed. An approved versioned
LightGBM model now scores entry candidates locally without an LLM/network call. Orders are blocked on stale
quotes, expired plans/risk decisions or insufficient expected edge after configured costs;
critical feed/order/reconciliation mismatches suspend the affected instrument. The current .NET
10 CI build is verified on GitHub Actions; see the workflow badge and Continuous integration
section below.

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
GET /api/risk/portfolio?accountId=...
GET /api/signals/allocations?accountId=...&instrumentId=...
GET /api/instruments
GET /api/rollout?instrumentId=...&count=...
GET /api/market-data/streams
GET /api/market-data/incidents
GET /api/market-data/latest
GET /api/market-data/collection
GET /api/market-data/tick-gaps?count=100
GET /api/ibkr/diagnostics
GET /api/ibkr/diagnostics?probeInstrumentId=US-STK-SPY-SMART
GET /api/datasets/manifest
GET /api/datasets/verify
GET /api/historical-backfill/jobs
GET /api/historical-backfill/gaps?instrumentId=US-STK-SPY-SMART
GET /api/replays
GET /api/replays/{replayRunId}
GET /api/replays/{replayRunId}/signals
GET /api/replays/backtests/{backtestId}
GET /api/research/candidates?instrumentId=US-STK-SPY-SMART&outcome=Accepted&count=100
GET /api/research/candidates/{candidateId}/labels
GET /api/research/evaluations?count=50
GET /api/research/evaluations/{evaluationId}
POST /api/research/evaluations
GET /api/research/calibrations?count=50
GET /api/research/calibrations/{calibrationId}
GET /api/models/numerical?count=50
GET /api/models/numerical/{modelId}
POST /api/rollout/{instrumentId}/evaluate?applyTransitions=false
POST /api/rollout/{instrumentId}/approve-live
POST /api/research/calibrations
POST /api/research/calibrations/{calibrationId}/decision
POST /api/research/calibrations/{calibrationId}/rank
POST /api/replays
POST /api/replays/{replayRunId}/pause
POST /api/replays/{replayRunId}/resume
POST /api/replays/{replayRunId}/cancel
POST /api/replays/{replayRunId}/backtests
```

`/api/health` and `/api/health/live` report web-process health. Dependency and trading readiness endpoints report database, IBKR, trading engine and OpenAI status separately, so the web app can remain healthy while trading is unavailable.
`/api/instruments` is a read-only view of configured instruments, persisted onboarding status, broker metadata and effective research/paper/live readiness.
The rollout worker resumes onboarding from persisted backfill checkpoints after restart. `GET /api/rollout?instrumentId=...&count=1` shows the latest concrete waiting reasons. `Collecting` requires completed backfill without reported gaps before warmup; `ResearchReady` additionally requires at least 50 final healthy bars per configured timeframe and 100 complete current-version labels per strategy by default. `ShadowReady` requires a completed compatible evaluation. `PaperReady` also requires an approved compatible model with a valid artifact hash and enough shadow decisions made after shadow entry. These thresholds can be changed with `InstrumentRollout:MinimumFinalBarsPerTimeframe` and `InstrumentRollout:MinimumCompleteLabelsPerStrategy`. Adding an enabled instrument in configuration starts registry sync, independent collection and historical backfill on application startup; trading remains separately gated. A persistent historical gap keeps research readiness pending until repaired. Model evaluation and approval use the explicit ModelOps workflow.
The market-data endpoints expose persisted quality checkpoints/incidents, the in-memory latest bid/ask/trade plus completed 5s/15s aggregates, and independent collection heartbeat/reconnect/lag/gap-fill state for every configured instrument/timeframe. With the official IBKR adapter, each enabled instrument also has an independent `TICK` collection stream and durable records of unknown tick coverage after interruptions or queue overflow. The tick-gap endpoint reports these intervals; minute-bar backfill does not restore missing ticks.
The IBKR diagnostics endpoint reports the compiled adapter type, official API assembly version, negotiated broker server version, connection and configured-account match, plus contract and feed state per instrument. It omits account identifiers and raw broker error text. Without `probeInstrumentId`, contract status is `NotProbed`; specifying one configured instrument sends a read-only `reqContractDetails` request when the real broker is connected. `Confirmed` means that one broker contract matched the configured symbol/security type/currency. Market-data `Available` requires a live collection stream and fresh bid/ask, while `BrokerError`, `NoBidAsk`, `StaleBidAsk` and `BrokerDisconnected` identify separate failures. Restrict access to operational endpoints until authentication is added.
The dataset endpoints expose the deterministic Parquet manifest and verify the manifest plus every listed file against SHA-256 hashes; they are read-only and never enable trading.
The historical-backfill endpoints expose durable per-instrument/timeframe checkpoints, retry and deduplication metrics, plus measured missing intervals.
The replay endpoints create and control isolated research runs over verified Parquet data. With an enabled short-signal profile, replay can also reconstruct 1s/5s/15s pattern signals from trade ticks using the same short-signal trigger rules as the live collector; a virtual timer evaluates a short bar at its window end plus allowed lateness even if no later tick arrives. The signal metadata records `replayAvailableAtUtc`; its source is the latest previously seen market event. Short replay stores accepted and filtered research candidates, links an accepted candidate to its signal by `SignalId`, and starts its label at actual availability using a fresh as-of bid/ask midpoint. Without that quote the label is `InsufficientData`. A completed run can be backtested with `POST /api/replays/{replayRunId}/backtests` and a `ReplayBacktestRequest` body; the response includes each signal's order/fill/exit result, simulated net P&L, and a separate horizon return. Signals execute in availability order using only quotes known at each decision time. A passive fill uses its posted limit, and a triggered exit cancels the unfilled entry remainder in the virtual broker. Entry timing never begins before the short bar is available. Stop/target distances are explicit backtest parameters; this research scenario does not reconstruct an approved production trade plan. The backtest re-verifies the exact input and signal output hashes, uses event-time bid/ask/trade ticks and persists an idempotent result in SQLite. Short replay refuses input whose receive-time order conflicts with event-time order rather than silently looking ahead. It never calls the broker or live order pipeline. Protect mutating replay endpoints with authentication, authorization and rate limiting before exposing the application publicly.
The research candidate endpoints expose accepted, rejected and blocked pattern evaluations plus their versioned as-of MFE/MAE, target/stop-first and after-cost labels. Evaluation runs perform version-locked walk-forward/holdout analysis with cost stress and segmented metrics. Calibration profiles fit monotonic probabilities only on pre-holdout data, verify threshold stability across walk-forward folds, compare once against the deterministic holdout baseline, and require an audited manual decision before they may rank opportunities. None of these endpoints enables trading. Protect all mutating research endpoints with authentication, authorization and rate limiting before public exposure.
The numerical-model endpoints are read-only. Training and manual promotion are internal service operations until authenticated operator controls exist. The production entry path defaults to an approved local ML.NET LightGBM artifact and fails closed when the model or exact feature version is unavailable; it does not call the LLM analyzer or critic.

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

CI builds and tests the real IBKR adapter using the pinned official DLL stored in the repository.
It also checks the published application contains the DLL, its runtime dependency and license/source files,
and verifies that a missing or changed API DLL fails validation. No TWS connection is opened by CI.

## Configuration

The additive forex configuration example is [forex-instruments.example.json](TradingBot.Web/forex-instruments.example.json).
`SecurityType=CASH`, `BaseCurrency=EUR`, `Currency=USD` and `Exchange=IDEALPRO` describe EUR/USD;
the internal symbol is `EURUSD`, separate from the broker symbol `EUR`. Each pair has its own
stable ID and explicit strategies. FX-3 resolves configured stock and forex contracts through IBKR;
FX-4 permits collection with per-instrument `Enabled=true` and `TradingEnabled=false`.
Forex execution remains blocked; stock defaults remain compatible.
The example is not loaded automatically. See [FX-2 rules and rollout](docs/EURUSD_READINESS.md#fx-2--konfiguracija-i-identitet).

To check configured contracts without starting the bot, collectors or order workflow, use a logged-in
paper TWS (7497) or paper Gateway (4002); TWS Read-Only API can remain enabled:

```powershell
dotnet run --project TradingBot.ShadowProbe --configuration Release -- --contracts-config TradingBot.Web/forex-instruments.example.json --output TestResults/fx3-contract-probe.json --port 7497 --client-id 93
```

Contract mode requires `AnalysisOnly`, global trading disabled and every instrument's `TradingEnabled=false`.
It also checks disabled FX instruments, exports requested/resolved identity and broker metadata without account
identifiers, and fails on missing, ambiguous or mismatched contracts. `Confirmed` proves contract identity only,
not feed entitlement, order eligibility or paper execution. See [FX-3 diagnostics](docs/EURUSD_READINESS.md#fx-3--ibkr-ugovori-i-dijagnostika).

For an isolated read-only FX data check, [forex-collection.example.json](TradingBot.Web/forex-collection.example.json)
enables EUR/USD collection and keeps the other entries disabled within that example only:

```powershell
dotnet run --project TradingBot.ShadowProbe --configuration Release -- --forex-data-config TradingBot.Web/forex-collection.example.json --output TestResults/fx4-data-probe.json --port 7497 --client-id 95 --seconds 30
```

This mode requires one enabled FX instrument, disabled global trading and `AnalysisOnly`. It creates a fresh
SQLite/Parquet directory next to the report, requests one recent open hour of MIDPOINT history, then runs the
existing quote/bar collectors for the requested duration. It starts no order or strategy workers. The report
separates `HistoryAndStoragePassed` from `LiveQuotesConfirmed`; exit code 0 confirms history/storage only.
Zero quotes during a closed session do not confirm the live feed. Both example files require explicit use;
they do not overwrite shared `appsettings.json` or the operational database.

FX uses `BidAsk` ticks and `MIDPOINT` bars with `useRTH=0`. Quote sizes are broker-reported base-currency
amounts, not trade volume. Bar volume is `null` in SQLite/Parquet; the additive candle migration preserves
existing stock rows and indexes. Forex collection follows the New York Sunday 17:15–Friday 17:00 week with
the daily 17:00–17:15 break, including US DST. Holiday exceptions remain a documented calendar limitation.
FX pattern publication and execution remain gated for later milestones. See [FX-4 evidence and limits](docs/EURUSD_READINESS.md#fx-4--prikupljanje-i-povijesna-dopuna).

FX-5 adds separate quote-midpoint aggregates for configured CASH instruments (5s/15s, optionally 1s).
Both halves must carry the same broker callback identity and receive time; the new source is
`IBKR.TickByTick.Forex.BidAsk.v2`. Legacy unpaired data is retained but cannot be guessed into a pair.
`ShortAggregates:ForexMaximumQuoteAgeSeconds` defaults to 2; allowed lateness defaults to 2 seconds.
Empty/known-invalid windows are not synthesized, and reconnects restart feature warm-up.
`ForexFeatureService.Compute` reads only final as-of aggregates and uses the existing indicator engine
with a separate `features-fx-midpoint-v1` contract. Volume/VWAP features remain null with explicit reasons.
`PriceFeaturesReady` indicates indicator warm-up, not permission to trade.
`IDeterministicReplayService.ReplayForexFeaturesAsync` replays verified paired Parquet quotes through
the same services and returns input hash, versions, feature frames and diagnostics; it does not run
stock patterns or simulate FX orders. The data probe above now includes aggregate counts and a 5s
feature snapshot. Local synthetic parity is verified; real streaming feature validation remains pending.
See [FX-5 rules and verification](docs/EURUSD_READINESS.md#fx-5--kratki-quote-agregati-i-featurei).

FX-6 adds explicit research evaluation of eight price patterns on FX quote-midpoint bars.
[forex-patterns.example.json](TradingBot.Web/forex-patterns.example.json) contains an optional
`ForexPatterns` section to merge into local configuration alongside the collection configuration;
it is not loaded automatically. Profiles are separate by instrument, strategy and 1s/5s/15s interval.
Their initial thresholds are research starting points, not calibrated trading parameters.
`ForexPatternResearchService.EvaluateAndPersistAsync` queries the completed FX bars and stores every
accepted/rejected evaluation in the existing SQLite research tables. `ForexPatternDetector.Evaluate`
is the deterministic, storage-free entry point for the same canonical input and as-of time.
Volume is explicitly unavailable and excluded from scoring; VWAP patterns are rejected with a reason.
FX research records have no stock labels. Automatic shadow signal routing/cooldown is described below; FX costs/labels
and full trade replay are handled separately by FX-8/FX-10. These services do not publish orders or start a broker connection.
See [FX-6 scope and evidence](docs/EURUSD_READINESS.md#fx-6--patterni-i-bodovanje).

FX-7 adds an opt-in automatic **shadow signal** worker, independent of tick collection and trading permission.
[forex-signals.example.json](TradingBot.Web/forex-signals.example.json) adds `ForexSignals:Profiles` alongside
matching `ForexPatterns` and configured CASH instruments. The example is disabled and has no approvals.
Stock and FX use the same deterministic pattern ranking code; thresholds, cooldown and version approvals
are isolated per instrument/strategy/interval. A one-second worker finalizes eligible bars even without a new tick.
It checks connection/session state, signal and paired-quote age, spread, warmed-up price features, ATR/price,
direction, confidence and exact approved signal/pattern/feature versions. The paired quote comes from the completed
bar, not independently updated latest bid/ask fields. SQLite stores every decision and restores cooldown/checkpoints.
Accepted decisions have `DecisionStage=FxShadowSignal`; they are never published to the stock order/model pipeline.
No active configuration or approval is changed by adding these files. See [FX-7 setup, evidence and limits](docs/EURUSD_READINESS.md#fx-7--strategije-i-signali).

FX-8 adds the explicit, deterministic `ForexRiskService.Evaluate(ForexRiskRequest)` assessment.
It sizes in base-currency units, rounds to reviewed broker increments, converts risk/costs/exposure to USD,
and includes minimum commissions, spread and slippage. Existing `RiskSettings` monetary limits are reused;
stock sizing and reservation behavior are preserved. Optional [forex-risk.example.json](TradingBot.Web/forex-risk.example.json)
profiles are not loaded automatically. Callers must supply a uniquely confirmed contract, reviewed order rules,
a fresh reconciled portfolio including pending commitments, paired bid/ask and required USD conversion quotes.
Missing inputs block the assessment. `Passed` is a research result; `ExecutionAuthorized` is always false.
FX-9 adds `ForexPaperPreparationService.PrepareAsync` for an explicitly approved FX plan: fresh broker rules,
account/portfolio inputs, the shared atomic reservation store and a durable assessment proof. The existing final
execution gate refreshes these inputs before the existing order/arbitration/exit lifecycle sends a paper order.
It supports long USD-quoted pairs with a verified USD account summary. `ForexPaperExecutionEnabled` defaults to false;
PaperReady, verified paper reconciliation, reviewed `ForexExecution:Profiles` limits and sufficient risk capacity are also required.
FX-7 shadow signals still do not automatically become orders: FX model approval/replay is FX-10, onboarding is FX-11.
FixedBracket, FX ATR trailing and the independent seconds timer have simulator coverage. Real IBKR fills and
venue acceptance of partial-size exits remain unverified until FX-12. See [FX-9 setup and limitations](docs/EURUSD_READINESS.md#fx-9--nalozi-i-pozicije).

- Database connection string: set in `TradingBot.Web/appsettings.json` or provide the environment variable `ConnectionStrings__TradingBot`.
- The persistence registration creates the configured SQLite directory; startup applies migrations.
- Supply account-specific configuration and API keys through environment variables or your local secret configuration. Keep shared settings free of credentials.
- `appsettings.Local.json` patterns are ignored for local use, but the host does not automatically load those files; use an explicitly configured provider or environment variables.
- Historical backfill defaults to 365 days of 1m, 730 days of 5m and 1825 days of 15m data, split into pacing-safe segments. Override without editing shared broker settings, for example `HistoricalBackfill__OneMinute__LookbackDays`, `HistoricalBackfill__OneMinute__SegmentDays`, `HistoricalBackfill__PacingDelayMilliseconds` and `HistoricalBackfill__MaximumAttemptsPerSegment`.
- Canonical raw/research events are written to `data/datasets` as Parquet by default and partitioned as `instrument=<id>/date=<UTC-date>/type=<bid|ask|trade|bar>`. Runtime output is ignored by Git. Override with `DatasetStorage__RootPath`; tune bounded backpressure and batching with `DatasetStorage__QueueCapacity`, `DatasetStorage__BatchSize` and `DatasetStorage__FlushIntervalSeconds`. Treat `DatasetStorage__SchemaVersion` as an immutable data-contract identifier for an existing dataset.
- Collection is enabled independently of AI and trading readiness. `MarketDataCollection__MonitorIntervalSeconds`, `HeartbeatGraceSeconds`, `HeartbeatIntervalMultiplier`, reconnect delays and `MaximumGapFillLookbackMinutes` tune per-stream recovery. `RegularSessionOnly=true` prevents expected overnight/weekend silence from being classified as stale; exchange holidays remain a calendar limitation.
- Deterministic replay is enabled by default. `Replay__EventsPerBatch`, `Replay__HistoryCandles`, `Replay__WorkerPollIntervalMilliseconds`, `Replay__MaximumDelayMilliseconds` and `Replay__MaximumSpeedMultiplier` tune its resource use. A run speed of `0` processes as fast as possible; a positive multiplier scales event-time delays.
- Canonical features use one versioned, as-of engine in live snapshots, pattern processing and replay. Indicator windows and regime/quote thresholds are configured through `CanonicalFeatures__*`; any setting change produces a different feature-version fingerprint and prevents an incompatible replay resume.
- Pattern detection uses the versioned `patterns-v2` contract. Each signal is isolated by instrument, strategy, direction, timeframe, pattern and event time; long and mirrored short rule thresholds are configurable through `PatternDetector__*`.
- Candidate labeling is enabled by default without requiring a shared settings file. Override worker cadence, writer grace, horizons, target/stop and cost assumptions with `CandidateLabeling__WorkerIntervalSeconds`, `CandidateLabeling__DataAvailabilityGraceSeconds`, `CandidateLabeling__HorizonsSeconds__0`, `CandidateLabeling__TargetMoveBps`, `CandidateLabeling__StopMoveBps`, `CandidateLabeling__CommissionPerSideBps`, `CandidateLabeling__SlippagePerSideBps` and `CandidateLabeling__FallbackRoundTripSpreadBps`. `CandidateLabeling__MaximumTickGapSeconds` defaults to 5; sparse ticks and ambiguous exit order are excluded. `CandidateLabeling__MaximumEntryQuoteAgeMilliseconds` defaults to 1000 for short-signal entry labels. `labels-v2` stores an exact input hash and simulated stop/target/timer net return separately from the horizon return. Any setting change produces a new label-version fingerprint; older labels require regeneration.
- Research evaluation defaults to 60 training days, 14 validation days, a 14-day non-overlapping step and a final untouched 30-day holdout. Configure these with `ResearchEvaluation__TrainingWindowDays`, `ValidationWindowDays`, `StepDays` and `HoldoutDays`; minimum sample sizes, `ConfidenceThresholds`, `CostStressMultipliers` and liquidity bounds are configurable through the same section. Every change produces a new `evaluation-v2` fingerprint. A run refuses mixed feature/pattern/label versions, purges overlapping label windows and selects the final threshold from walk-forward validation before inspecting holdout.
- Research calibration uses a versioned `calibration-v1` isotonic map and defaults to at least 100 pre-holdout observations, three completed folds and 20 observations per initial calibration bin. `ResearchCalibration__MaximumStableThresholdRange`, `StableThresholdTolerance`, `MinimumFoldAgreementRatio` and baseline-degradation settings control the fail-closed approval gate. Any change creates a different calibration fingerprint; approval requires the exact output SHA-256, reviewer and reason.
- Signal arbitration defaults to conflict policy `Reject`, permits same-direction scale-in by different strategies, rejects duplicate active allocations from the same strategy and limits active allocations per instrument to three. Override with `SignalArbitration__ConflictPolicy`, `SignalArbitration__AllowSameDirectionScaleIn`, `SignalArbitration__RejectSameStrategyWhileActive`, `SignalArbitration__MaximumActiveAllocationsPerInstrument`, `SignalArbitration__IntentTimeoutSeconds` and `SignalArbitration__StrategyPriorities__<strategy-id>`. A credential-free example is in `TradingBot.Web/signal-arbitration.example.json`.
- Numerical modeling defaults to local approved-model scoring in the entry path. `NumericalModel__UseApprovedModelForEntry`, LightGBM hyperparameters, minimum sample/selection counts, required baseline improvements and candidate probability thresholds are configurable. The model verifies its frozen evaluation input before training; its final probability cutoff comes from temporal validation. `NumericalModel__PlanningTargetMoveBps` is a separate entry-planning assumption, not an individual prediction inferred from historical mean returns. A credential-free example is in `TradingBot.Web/numerical-model.example.json`. Keep the default enabled for production; disabling it restores the legacy network LLM path.
- `NumericalModel__CacheRefreshSeconds` (default 1) controls how often the local inference cache checks the approved model identity in SQLite; unchanged model artifacts stay loaded. Approval and rollback within the process invalidate the cache immediately. Prediction checks the exact instrument, strategy, feature, pattern and current label configuration.

### Local model operations

`TradingBot.ModelOps` runs research/model commands without starting the Web host or broker workers. Run it from the repository root against an explicitly selected existing SQLite database. Review the model snapshot, holdout metrics and `outputSha256` before approving or rolling back. Keep the same runtime configuration or pass `--config` to every command.

```powershell
$dbPath = 'C:\absolute\path\to\trading.db'
$evaluationId = '<evaluation-guid-from-evaluate>'
$candidateId = '<candidate-model-guid-from-train>'
$previousId = '<previous-model-guid-from-list>'
$candidateHash = '<candidate-output-sha256-from-show>'
$previousHash = '<previous-output-sha256-from-show>'
dotnet run --project TradingBot.ModelOps -- migrate --db $dbPath
dotnet run --project TradingBot.ModelOps -- evaluate --db $dbPath --instrument-id US-STK-SPY-SMART --strategy-id deterministic-patterns --horizon-seconds 60 --from 2026-01-01T00:00:00Z --to 2026-09-30T00:00:00Z
dotnet run --project TradingBot.ModelOps -- train --db $dbPath --evaluation-id $evaluationId
dotnet run --project TradingBot.ModelOps -- compare --db $dbPath --left $previousId --right $candidateId
dotnet run --project TradingBot.ModelOps -- approve --db $dbPath --model-id $candidateId --reviewer 'operator' --reason 'Reviewed holdout evidence' --sha256 $candidateHash
dotnet run --project TradingBot.ModelOps -- history --db $dbPath --model-id $candidateId
dotnet run --project TradingBot.ModelOps -- rollback --db $dbPath --model-id $previousId --reviewer 'operator' --reason 'Revert after review' --sha256 $previousHash
```

`reject` uses the same arguments as `approve`. `list` and `show --model-id` display stored model results; `compare` requires the same instrument, strategy, horizon and pipeline versions. Use `--replay-run-id` with `evaluate` to select replay candidates. Back up and review the real operational database before running `migrate`; this step was verified only on a disposable SQLite database.

### Required IBKR dependency

The official `CSharpAPI.dll` version **10.49.02.0** is tracked in
[`third_party/ibkr/10.49.02`](third_party/ibkr/10.49.02/README.md), together with its source archive,
license and provenance. A normal Git clone includes it. No separate API installation or
`C:\TWS API` path is needed for restore/build/publish. `Google.Protobuf` 3.29.5 is restored through NuGet.

The real adapter is always compiled and registered. Restore/build checks the pinned DLL's SHA-256:
`IBKR001` means the required file is missing; `IBKR002` means its contents have changed. Restore
the tracked dependency from Git, or review an intentional API upgrade with its matching source,
license, checksum and tests. There is no broker-unavailable build mode. Test doubles remain available
for isolated tests and do not replace the default production adapter.

```powershell
dotnet build TradingBot.sln -c Release
dotnet test TradingBot.sln -c Release
dotnet publish TradingBot.Web -c Release -o ./dist/tradingbot
# Complete dependency and publish verification without starting the bot:
./scripts/verify-ibkr-adapter.ps1
```

The publish folder includes `CSharpAPI.dll`, `Google.Protobuf.dll` and the accompanying
`third_party/ibkr` files. Deploy the entire publish folder. The verification script checks these
files, the runtime dependency manifest, all solution tests and the missing/changed-DLL guards.
It does not start the web app or connect to TWS. TWS/IB Gateway, API access and the appropriate
market-data permissions are still needed for a real broker connection; compilation is not a paper E2E test.

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
- `RiskEngine`, `PositionSizer` and the durable portfolio reservation service implement deterministic account, daily-loss, gross/net exposure, per-instrument/per-strategy, cooldown and correlation checks. R01–R02 are fixed; every production approval atomically reserves capacity before it reaches order execution.
- `SignalArbitrationService` deduplicates concurrent signals, applies explicit `Reject`/`Priority` conflict policy without silent netting, atomically claims approved intents before broker submission and maintains durable per-strategy virtual allocations. Exit management validates each exit against that signal's open virtual quantity; `GET /api/signals/allocations` exposes the ledger.
- `OrderManager` persists a stable business intent before broker submission, merges callback-first status, separates cumulative order state from immutable executions, applies late commissions and waits for confirmed cancellation. Entry and exit submission remain idempotent across restart.
- Broker-state reconciliation gates startup readiness: trading remains disabled until IBKR positions/open orders are compared with SQLite state, including unresolved intents, cancel-pending orders and incompletely protected positions.
- Market data subscription startup subscribes to every enabled `TradingSettings.Instruments` entry using its configured timeframes once the engine is ready. Historical seeding is no longer on this live startup path; a separate resumable worker performs the wide backfill in parallel. The checked-in fail-closed example contains SPY on `1m`, `5m` and `15m`; legacy `Symbols` remains a temporary compatibility fallback.
- A persistent instrument registry synchronizes configuration at startup, records every onboarding status transition and preserves progress across restarts. New enabled instruments begin at `BackfillPending`; configuration changes reset onboarding safely, while removed instruments remain disabled for audit.
- Instrument-level `TradingEnabled` is only a requested permission. Paper/live order submission also requires the matching persisted readiness state, and no startup path automatically grants `LiveEnabled`.
- Canonical `market-data-v2` events carry instrument ID, event/receive time, source, sequence and finality for bid, ask, trade and bar data. Persisted stream state makes stale, future, duplicate, out-of-order and gap conditions explicit.
- Historical backfill persists jobs, segment attempts and gap reports per instrument/timeframe. Restart resumes the saved boundary, duplicate candles are ignored by the database unique key, and onboarding advances only through `Backfilling` to `Collecting` after every configured timeframe finishes.
- Deterministic replay verifies and hashes its exact Parquet input, processes events in event/receive/ID order with as-of-only candle windows, and persists run checkpoints plus isolated signals. Pause/resume survives a new service instance; replay has no event-bus, broker or order dependency. A separate event-time virtual broker backtests completed long signals with the real position sizer, configured entry policy, quote freshness/spread gate, fill latency, finite displayed liquidity, partial fills, minimum commissions, slippage and fixed stop/target/timer exits. Signals without sufficient tick/quote coverage remain unfilled or unresolved; bar OHLC alone never proves a fill.
- Research calibration converts raw pattern confidence into monotonic empirical probabilities, measures Brier/log-loss/ECE out of sample, derives a fold-stable confidence threshold and stores immutable model artifacts. Only a manually approved version may rank competing opportunities, and exact pipeline versions plus profile hash are checked on every ranking call. The ranker is research-only and is not connected to risk or order submission.
- Numerical model training compares ML.NET LightGBM with the deterministic confidence baseline using purged walk-forward folds and one untouched holdout after costs. Versioned artifacts require reviewed-hash manual approval; production inference is local, version-locked and fail-closed, while LLM services remain outside the default entry hot path.
- Only final `Healthy` candles reach pattern analysis. Gap candles may be retained for research but are blocked from trading; a second guard in the pattern worker rejects any unhealthy candle.
- Healthy level-one events maintain latest bid/ask/trade, spread and completed 5s/15s trade aggregates; `MarketSnapshotService` combines them with its 1m/5m/15m candle context. `ShortAggregates` can enable optional 1s bars and configure allowed lateness and bounded history. UTC windows close at window end plus allowed lateness; empty windows are counted but never synthesized, and trades received after closure cannot revise completed bars. Consumers must use `AvailableAtUtc` and `GetCompletedShortAggregates(..., asOfUtc)` when calculating signals or replay features.
- Canonical EMA and RSI use the pinned `FacioQuo.Stock.Indicators` adapter under `features-v3`. The existing rolling true-range ATR is retained because it differs from the library's Wilder ATR. Previously approved `features-v2` models require a new evaluation and approval before entry can resume.
- Optional `ShortSignals` profiles evaluate existing patterns on completed 1s/5s/15s trade bars for a configured instrument and strategy. Each profile controls allowed pattern types, confidence, contiguous warmup bars, cooldown and maximum signal age. The short candidate enters the existing pattern decision, numerical model, strategy, portfolio-risk and signal-arbitration path; 1m/5m/15m candles remain its broader context. Short candidates require a separately approved model with the interval-specific feature contract (for example `features-v3+config-…+short-5s-v1`), and the short path never invokes the LLM. See `TradingBot.Web/short-signals.example.json`; add its strategy ID to the instrument before enabling it. The default has no active profiles, and `AnalysisOnly` remains the default operating mode.
- Channel-based background services connect the pipeline without a giant trading loop: candle pattern detection, pattern decisioning and approved order execution run as focused async consumers.
- The Blazor dashboard shows engine/broker/market/AI/risk/P&L status with realtime refresh and durable controls for pause, resume, coordinated close-current-position and kill switch.
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
- `TradingBot.Infrastructure/Services/MarketDataQualityService.cs`
- `TradingBot.Infrastructure/Services/DeterministicReplayService.cs`
- `TradingBot.Infrastructure/Services/LatestMarketDataService.cs`
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
- `TradingBot.Infrastructure/Services/InstrumentRegistryService.cs`
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
- `TradingBot.Infrastructure/Background/DeterministicReplayHostedService.cs`
- `TradingBot.Infrastructure/Background/InstrumentRegistryHostedService.cs`
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
- `TradingBot.Application/Interfaces/IInstrumentRegistryService.cs`
- `TradingBot.Application/Interfaces/IMarketDataQualityService.cs`
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
- `TradingBot.Tests/InstrumentRegistryServiceTests.cs`
- `TradingBot.Tests/CanonicalMarketDataTests.cs`
- `TradingBot.Tests/DeterministicReplayServiceTests.cs`

Short technical notes for Codex

- EF Core: uses `IDbContextFactory<TradingBotDbContext>` and SQLite. Candle entity is uniquely indexed on `(Symbol, Timeframe, TimestampUtc)` to prevent duplicates.
- Event bus: `TradingEventBus` uses bounded `Channel<T>` with configurable full-mode strategies (Wait, DropOldest, DropNewest, Reject). Keep `TryPublish` non-blocking for IB callbacks.
- IBKR: `IIbkrAdapter` is the broker boundary. The conditional concrete adapter uses the official TWS C# API when available, publishes completed streaming bars and requests level-one bid/ask/last events from the 1m subscription. The standard CI build cannot compile this conditional path without the official DLL.
- Canonical market data: `MarketDataQualityService` persists stream checkpoints and incidents, blocks non-final/stale/future/duplicate/out-of-order/invalid events, and retains gap bars only for research. Raw quote/trade events and validated historical bars flow through a bounded writer into partitioned Parquet; SQLite remains limited to operational state, incidents and canonical candles.
- Dataset storage: `ParquetMarketDatasetStore` writes atomic partition files, a stable `_manifest.json` and `_manifest.sha256`, records a SHA-256 for each file, verifies integrity before reads, and returns deterministically ordered/de-duplicated events for instrument/time/type ranges.
- Collection reliability: each enabled instrument/timeframe owns an isolated bar subscription loop. It records heartbeat and receive lag, reconnects only the failed stream, keeps collection active in `AnalysisOnly`/pause/AI failure states, and invokes pacing-safe historical bar gap fill before resubscription. The official IBKR adapter separately subscribes each enabled instrument to `reqTickByTickData` `AllLast` and `BidAsk`; broker event time, local receive time, price, and available size enter the canonical Parquet dataset through a bounded channel. Tick failures and queue overflow reconnect only the affected instrument and are recorded as unknown coverage in SQLite.
- Deterministic replay: `DeterministicReplayService` reads verified Parquet bars, freezes the exact input with SHA-256 plus pipeline contract versions, reconstructs pattern-detector state at checkpoints and stores isolated replay signals. It never publishes to `ITradingEventBus` and cannot reach order execution.
- Canonical features: `FeatureEngine` filters every input to its explicit `AsOfUtc`, deterministically orders and de-duplicates candles, rejects future/stale quotes and calculates the same EMA, RSI, ATR, VWAP, relative volume, spread, momentum, mean-reversion, regime and normalized liquidity/volatility values for live, backfill and replay inputs.
- Market data startup: `TradingSettings.Instruments` defines stable instrument IDs, broker metadata, allowed directions, strategy IDs, per-instrument timeframes and optional limits. `MarketDataSubscriptionHostedService` seeds and subscribes every enabled instrument. `TradingEnabled` defaults to false; `TradingExecutionGuard` joins it with persisted per-instrument readiness before paper/live submission, while global `AnalysisOnly` remains active.
- Instrument onboarding: `InstrumentRegistryService` idempotently synchronizes configuration into SQLite, preserves progress and broker metadata across restarts, audits explicit status transitions and resets readiness when data-affecting configuration changes. Historical backfill resumes from durable checkpoints, emits validated bars to the research dataset, and advances instruments to collection without granting trading permission.
- Pipeline identity: every pattern has a deterministic `SignalId` plus a per-run `CorrelationId`, `InstrumentId`, `StrategyId` and explicit market-data/feature/pattern/strategy contract versions. The same context is propagated into strategy and risk audit JSON.
- Pattern detection: `patterns-v2` evaluates Hammer/Shooting Star, Bullish/Bearish Engulfing, Double Bottom/Top, Breakout/Breakdown and Retest, and VWAP Reclaim/Reject. Every evaluation exposes hard-condition results, weighted score components and reasons. Live configuration can evaluate multiple strategy IDs and directions in parallel without cross-instrument/timeframe deduplication; short candidates remain research-only until downstream short strategy, risk, borrow and execution support is implemented.
- Market snapshots: `IMarketSnapshotService` combines 15m broader direction, 5m setup/pullback context and 1m entry timing into a structured `MarketSnapshot` for later AI analysis. It includes candles, features, patterns, support/resistance candidates, trend, volume and volatility, and automatically uses the latest healthy canonical trade/bid/ask for current price and spread when available.
- AI analysis: `IAiMarketAnalyzer` is analysis-only. It sends structured snapshot data to OpenAI using JSON schema response format, validates the response, logs duration/token usage when available, persists the analysis to SQLite and defaults to no trade (`REJECT`) on unavailable or invalid AI output.
- AI trade criticism: `IAiTradeCritic` is rejection-focused. It reviews the snapshot, detected pattern and AI analysis for risks such as higher-timeframe weakness, sell volume, false breakout risk, poor reward/risk, excessive volatility, poor liquidity, large spread, timeframe conflicts and weak pattern structure. It never submits orders and defaults to rejected when unavailable or invalid.
- AI cost controls: `PatternQualityGate` uses `PatternDetectorOptions.MinimumTradeSetupQuality` and per-pattern thresholds to decide whether a candidate reaches OpenAI. `TradingSettings.MinimumPatternQualityForAiAnalysis` remains in settings/telemetry but is not the active gate. The critic runs only for an actionable, sufficiently confident BUY; WAIT, REJECT and safe fallbacks skip it.
- AI context sizing: `OpenAiSettings.MarketContext` controls rich snapshot candle history sent to AI. Defaults are 60 one-minute candles, 50 five-minute candles and 40 fifteen-minute candles while preserving OHLCV, indicators, volume, volatility, VWAP, support/resistance, trend and pattern context.
- AI usage telemetry: analyzer/critic calls store token, model, prompt-version, duration and success/failure records. Retries currently share one record per completed service call rather than one per HTTP attempt (R15). Dashboard pricing is configurable and is not an authoritative bill.
- Strategy engine: `IStrategyEngine` receives the snapshot, pattern, AI analysis and critic result. It creates a `TradeSignal` only when configured rules pass for AI confidence, pattern quality, critic approval, reward/risk, spread, market regime and trading hours. It persists all decisions, including rejected setups and reasons, and leaves final position sizing to later risk management.
- Risk engine: `IRiskEngine` receives the verified operating-mode account, current positions/open orders and closed trades from the current New York session. `IPositionSizer` rejects any zero mandatory capacity; `IPortfolioRiskService` atomically persists pending/committed reservations and applies global gross/net, per-instrument, per-strategy, buying-power, leverage, cooldown and configured correlation-group limits. `GET /api/risk/portfolio` exposes active reservations.
- Order management: `IOrderManager` supports durable approved intents, parent-linked bracket orders, confirmed cancellation, structured status, immutable individual executions and late commission persistence. Exit management coordinates stop/target/time/operator exits and caps aggregate exit fills at the entry-filled quantity; R03–R07 and R12 are resolved in point 15.
- Broker reconciliation: startup state moves through `Starting`, `Connecting`, `Reconciling`, then `Ready` only when broker positions/open orders match SQLite open trades/orders. Mismatches such as broker-only SPY shares or local-only open orders keep the engine `Degraded` or `Faulted` with trading disabled. The web app exposes `GET /api/reconciliation/status` for readiness/status monitoring.
- Background pipeline: hosted services consume channels with cancellation propagation and per-message exception isolation. `CandlePatternDetectionBackgroundService` consumes candles and publishes patterns. `PatternDecisionBackgroundService` gates on engine `Ready` plus `TradingSettings.Enabled`, builds snapshots and runs AI/critic/strategy/risk before publishing approved plans. `ApprovedOrderExecutionBackgroundService` consumes approved plans and calls `IOrderManager`, which uses the centralized execution guard before any broker submission.
- Dashboard: the Blazor home page shows engine, broker, market, AI, risk and position status. Controls call `ITradingControlService`; pause/kill permission survives reconnect, Close Current Position submits a coordinated exit, and Kill requests cancellation of open orders.
- Trade analytics: `/trades` lists recent trades with timestamp, symbol, pattern, AI confidence, entry/exit, quantity, gross P/L, fees, net P/L and result. `/patterns` shows aggregate pattern statistics plus decision audit rows linking pattern candidates, AI/critic/strategy/risk decisions and actual outcomes. Read services use bounded EF Core projections/grouping rather than loading whole tables for statistics.
- Operating modes: `AnalysisOnly` stores hypothetical trades instead of submitting broker orders. `PaperTrading` requires a broker environment verified as paper; `LiveTrading` additionally requires `LiveTradingExplicitlyEnabled`. Durable pause/kill permission is implemented; a runtime account-context change still requires a separately verified broker reconnect/reconciliation cycle.
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

Read-only TWS paper feed diagnostics and point 30 thresholds are in [docs/SHADOW_READINESS.md](docs/SHADOW_READINESS.md). The real-feed shadow assessment is still in progress.

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
