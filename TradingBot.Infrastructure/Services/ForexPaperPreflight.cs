using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

public sealed record ForexPaperCheck(string Code, bool Passed, string Detail);
public sealed record ForexPaperPreflightReport(DateTime CheckedAtUtc, string InstrumentId, string StrategyId,
    bool SessionOpen, DateTime? NextScheduledSessionUtc, IReadOnlyList<ForexPaperCheck> Checks)
{
    public string Scope => "Offline file/database preflight only; no broker connection, migration, approval or order submission.";
    public bool OfflinePrerequisitesPassed => Checks.All(x => x.Passed);
    public bool CanSendOrders => false;
    public string[] RuntimeChecksStillRequired => ["Verified paper account and USD summary", "Fresh live paired quotes and healthy current rollout",
        "Broker size/price rules, margin and partial-size protective exits", "Existing positions/orders reconciled",
        "Bounded test limits and stop procedure", "Fresh approved signal, FX-9 sizing, costs, reservation and execution guard"];
}

/// <summary>Read-only evidence inventory. A successful report never authorizes an order.</summary>
public static class ForexPaperPreflight
{
    public static async Task<ForexPaperPreflightReport> CheckAsync(IConfiguration config, string databasePath,
        string instrumentId, string strategyId, DateTime now, CancellationToken ct = default)
    {
        if (now.Kind != DateTimeKind.Utc || !Path.IsPathFullyQualified(databasePath)
            || string.IsNullOrWhiteSpace(instrumentId) || string.IsNullOrWhiteSpace(strategyId))
            throw new ArgumentException("Absolute database path, instrument/strategy IDs and UTC clock are required.");
        var checks = new List<ForexPaperCheck>();
        void Add(string code, bool passed, string detail) => checks.Add(new(code, passed, detail));
        var session = MarketDataSession.IsForexOpen(now);
        Add("session", session, session ? "Configured FX session is open; broker availability still needs verification." : "Configured FX session is closed.");
        DateTime? next = null;
        if (!session)
        {
            var at = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
            while (at < now.AddDays(8) && !MarketDataSession.IsForexOpen(at)) at = at.AddMinutes(1);
            if (at < now.AddDays(8)) next = at;
        }
        ForexResearchContract? contract = null;
        string? feature = null, pattern = null, label = null;
        int horizon = 0;
        var evaluationSettings = new ResearchEvaluationSettings();
        try
        {
            evaluationSettings = config.GetSection("ResearchEvaluation").Get<ResearchEvaluationSettings>() ?? new();
            evaluationSettings.ConfidenceThresholds = config.GetSection("ResearchEvaluation:ConfidenceThresholds").Get<decimal[]>() ?? evaluationSettings.ConfidenceThresholds;
            evaluationSettings.CostStressMultipliers = config.GetSection("ResearchEvaluation:CostStressMultipliers").Get<decimal[]>() ?? evaluationSettings.CostStressMultipliers;
            var trading = config.GetSection("TradingSettings").Get<TradingSettings>() ?? new();
            Add("no-live", trading.OperatingMode != TradingOperatingMode.LiveTrading && !trading.LiveTradingExplicitlyEnabled,
                "Live mode and live explicit enablement must remain off.");
            var instruments = trading.GetConfiguredInstruments();
            var instrument = instruments.SingleOrDefault(x => x.InstrumentId == instrumentId);
            Add("instrument", trading.GetValidationErrors().Count == 0 && instrument is { Enabled: true, InstrumentType: InstrumentType.Forex, Currency: "USD" }
                && instrument.StrategyIds.Contains(strategyId) && instrument.AllowedDirections.Contains(TradeDirection.Long),
                "An enabled USD-quoted CASH instrument with the selected long strategy is required; collection does not require trading enablement.");
            Add("isolated-execution", instruments.All(x => x.InstrumentId == instrumentId || !x.TradingEnabled),
                "Other instruments must not be enabled for execution during the bounded pilot; collection may remain enabled.");
            var patterns = config.GetSection("ForexPatterns").Get<ForexPatternSettings>() ?? new();
            patterns.ReplaceConfiguredArrays(config.GetSection("ForexPatterns"));
            var signals = config.GetSection("ForexSignals").Get<ForexSignalSettings>() ?? new();
            var aggregates = config.GetSection("ShortAggregates").Get<ShortAggregateSettings>() ?? new();
            aggregates.IntervalsSeconds = config.GetSection("ShortAggregates:IntervalsSeconds").Get<int[]>() ?? aggregates.IntervalsSeconds;
            var risk = config.GetSection("ForexRisk").Get<ForexRiskSettings>() ?? new();
            var research = config.GetSection("ForexResearch").Get<ForexResearchSettings>(x => x.ErrorOnUnknownConfiguration = true) ?? new();
            contract = new(Microsoft.Extensions.Options.Options.Create(research), Microsoft.Extensions.Options.Options.Create(risk), Microsoft.Extensions.Options.Options.Create(config.GetSection("RiskSettings").Get<RiskSettings>() ?? new()),
                Microsoft.Extensions.Options.Options.Create(trading), Microsoft.Extensions.Options.Options.Create(patterns), Microsoft.Extensions.Options.Options.Create(signals), Microsoft.Extensions.Options.Options.Create(aggregates),
                Microsoft.Extensions.Options.Options.Create(config.GetSection("CanonicalFeatures").Get<CanonicalFeatureSettings>() ?? new()));
            try
            {
                var c = contract.Get(instrumentId, strategyId); feature = c.Feature; pattern = c.Pattern; label = c.Label; horizon = c.Profile.HoldingSeconds;
                Add("research-contract", risk.Validate().Count == 0, "Explicit FX research, fee and liquidity assumptions must be complete and valid.");
                var signal = signals.Profiles.SingleOrDefault(x => x.Selection.InstrumentId == instrumentId && x.Selection.StrategyId == strategyId
                    && x.Selection.IntervalSeconds == c.Profile.IntervalSeconds);
                Add("signal-approval", patterns.Validate().Count == 0 && signals.Validate(trading, aggregates, patterns).Count == 0
                    && signal?.Selection.Enabled == true && signal.ApprovedFeatureVersion == feature && signal.ApprovedSignalVersion == signal.Version
                    && signal.ApprovedPatternVersion + "+" + signal.Version == pattern, "Exact feature/pattern/signal versions require explicit approval.");
                var exit = config.GetSection("ExitStrategy").Get<ExitStrategySettings>() ?? new();
                Add("exit-contract", instrument?.MaximumHoldingSeconds == horizon && exit.Mode == c.Profile.Exit.Mode
                    && exit.BreakEvenTriggerR == c.Profile.Exit.BreakEvenTriggerR && exit.BreakEvenOffsetR == c.Profile.Exit.BreakEvenOffsetR
                    && exit.TrailingActivationR == c.Profile.Exit.TrailingActivationR && exit.TrailingAtrMultiplier == c.Profile.Exit.TrailingAtrMultiplier,
                    "Configured paper exit and holding seconds must match the researched policy.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { Add("research-contract", false, "FX research contract is missing, incomplete or inconsistent. No defaults were approved."); }
            var execution = config.GetSection("ForexExecution").Get<ForexExecutionSettings>() ?? new();
            execution.NormalizeBoundTimestamps();
            try
            {
                execution.Validate();
                var rules = execution.Profiles.SingleOrDefault(x => x.InstrumentId == instrumentId);
                Add("reviewed-size-limits", rules != null && rules.ReviewedAtUtc <= now && now - rules.ReviewedAtUtc <= TimeSpan.FromHours(24),
                    "Reviewed venue base-unit minimum/maximum and evidence must be present and no older than 24 hours; broker rules still need a live refresh.");
            }
            catch (ArgumentException) { Add("reviewed-size-limits", false, "Reviewed FX execution limits are invalid or missing an explicit timestamp offset."); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { Add("configuration", false, "Configuration is invalid; review typed FX configuration locally. Raw values are not exported."); }

        Add("database", File.Exists(databasePath), "An existing operational database is required; this command never creates or migrates one.");
        if (File.Exists(databasePath))
        {
            try
            {
                var connection = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
                await using var db = new TradingBotDbContext(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var registered = await db.InstrumentRegistryRecords.AsNoTracking().SingleOrDefaultAsync(x => x.InstrumentId == instrumentId, ct);
                Add("paper-ready", registered is { ConfiguredEnabled: true, Status: InstrumentOnboardingStatus.PaperReady },
                    "Registry must already be PaperReady from FX-11 evidence; this command never changes it.");
                var rollout = await db.InstrumentRolloutEvaluationRecords.AsNoTracking().Where(x => x.InstrumentId == instrumentId)
                    .OrderByDescending(x => x.EvaluatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
                Add("current-rollout", rollout != null && rollout.StatusAfter == InstrumentOnboardingStatus.PaperReady && !rollout.Suspended
                    && rollout.EvaluatedAtUtc <= now && now - rollout.EvaluatedAtUtc <= TimeSpan.FromSeconds(60)
                    && rollout.UnhealthyStreamCount == 0 && rollout.UnknownOrderCount == 0,
                    "An unexpired FX-11 rollout assessment must retain PaperReady; its freshness limit is 60 seconds.");
                var models = await db.NumericalModelRecords.AsNoTracking().Where(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId
                    && x.Status == NumericalModelStatus.Approved).ToArrayAsync(ct);
                var model = models.Length == 1 ? models[0] : null;
                var modelValid = model != null && feature != null && model.FeatureVersion == feature && model.PatternVersion == pattern
                    && model.LabelVersion == label && model.HorizonSeconds == horizon && model.ModelVersionTag == ForexResearchContract.Model
                    && model.ApprovalReady && model.ModelArtifact.Length > 0 && model.OutputSha256 == NumericalModelService.OutputHash(model);
                Add("approved-model", modelValid, "Exactly one approved FX model must match the current research contract and artifact hash.");
                var evaluation = model == null ? null : await db.ResearchEvaluationRunRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.EvaluationRunId, ct);
                var replay = evaluation?.ReplayRunId == null ? null : await db.ReplayRunRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == evaluation.ReplayRunId, ct);
                Add("research-integrity", modelValid && evaluation != null && evaluation.InstrumentId == instrumentId && evaluation.StrategyId == strategyId
                    && evaluation.FeatureVersion == feature && evaluation.PatternVersion == pattern && evaluation.LabelVersion == label && evaluation.HorizonSeconds == horizon
                    && evaluation.InputSha256.Length > 0 && evaluation.EvaluationVersion == ResearchEvaluationVersion.Create(evaluationSettings)
                    && evaluation.CompletedAtUtc != default && evaluation.OutputSha256 == ResearchEvaluationService.ComputeOutputHash(evaluation)
                    && replay?.Status == ReplayRunStatus.Completed && replay.InstrumentId == instrumentId && replay.StrategyId == strategyId
                    && replay.OutputSha256 == await DeterministicReplayService.ComputeOutputHashAsync(db, replay.Id, ct),
                    "Approved model must reference an intact completed FX evaluation and replay.");
                var active = await db.PortfolioRiskReservationRecords.CountAsync(x => x.Status == PortfolioReservationStatus.Pending || x.Status == PortfolioReservationStatus.Committed, ct);
                Add("local-capacity-clear", active == 0, "The isolated pilot requires no existing active local reservations; actual broker positions/orders require separate reconciliation.");
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or System.Text.Json.JsonException)
            { Add("database-readable", false, "Operational schema/evidence could not be read consistently; no migration or repair was attempted."); }
        }
        return new(now, instrumentId, strategyId, session, next, checks);
    }
}
