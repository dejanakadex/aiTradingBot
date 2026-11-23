using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

internal static class ForexResearchPreparation
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static (string Config, object Report) Prepare(string sourceJson, string? rulesJson, decimal equityUsd)
    {
        if (equityUsd is <= 0 or > 1_000_000) throw new ArgumentException("Explicit simulated equity must be in (0, 1,000,000] USD.");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sourceJson));
        var source = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var trading = source.GetSection("TradingSettings").Get<TradingSettings>() ?? throw new ArgumentException("TradingSettings required.");
        if (trading.Enabled || trading.LiveTradingExplicitlyEnabled || trading.ForexPaperExecutionEnabled
            || trading.OperatingMode != TradingOperatingMode.AnalysisOnly || trading.GetConfiguredInstruments().Any(x => x.TradingEnabled)
            || trading.GetValidationErrors().Count != 0)
            throw new ArgumentException("Preparation requires a valid disabled AnalysisOnly collection configuration.");
        var evidence = rulesJson == null ? null : JsonNode.Parse(rulesJson) ?? throw new ArgumentException("Rules evidence required.");
        if (evidence != null && (evidence["Passed"]?.GetValue<bool>() != true || evidence["CanSendOrders"]?.GetValue<bool>() != false))
            throw new ArgumentException("A successful read-only rules capture is required.");
        var snapshot = evidence == null ? null : evidence["Snapshot"]?.Deserialize<ForexBrokerRulesSnapshot>(Json)
            ?? throw new ArgumentException("Rules snapshot missing.");
        var instrumentId = evidence == null
            ? trading.GetEnabledInstruments().Single(x => x.InstrumentType == InstrumentType.Forex).InstrumentId
            : evidence["InstrumentId"]?.GetValue<string>() ?? throw new ArgumentException("Rules instrument missing.");
        var instrument = trading.GetEnabledInstruments().SingleOrDefault(x => x.InstrumentId == instrumentId);
        if (instrument?.InstrumentType != InstrumentType.Forex || instrument.Currency != "USD" || instrument.StrategyIds.Count != 1)
            throw new ArgumentException("Select one enabled USD-quoted FX instrument with one strategy.");
        var strategyId = instrument.StrategyIds.Single();
        var metadata = snapshot?.Contract?.Metadata;
        var rules = snapshot?.Rules;
        if (snapshot != null && (snapshot.Contract?.Status != "Confirmed" || snapshot.Contract.MatchCount != 1
            || snapshot.Contract.ContractId is not > 0 || metadata?.Contract != IbkrContractFactory.Describe(instrument)
            || metadata.MinimumTick is not > 0 || metadata.MinimumSize is not > 0 || metadata.SizeIncrement is not > 0
            || rules == null || rules.ContractId != snapshot.Contract.ContractId
            || snapshot.ObservedUtc.Kind != DateTimeKind.Utc || rules.VerifiedAtUtc.Kind != DateTimeKind.Utc
            || snapshot.ObservedUtc > DateTime.UtcNow
            || rules.VerifiedAtUtc > snapshot.ObservedUtc || string.IsNullOrWhiteSpace(rules.EvidenceSource)
            || rules.MinimumBaseUnits < metadata.MinimumSize || rules.MaximumBaseUnits < rules.MinimumBaseUnits
            || rules.BaseUnitIncrement <= 0 || rules.BaseUnitIncrement % metadata.SizeIncrement.Value != 0
            || rules.PriceBands is not { Count: > 0 }))
            throw new ArgumentException("Rules capture has invalid contract, timestamps or quantity evidence.");

        var aggregates = source.GetSection("ShortAggregates").Get<ShortAggregateSettings>() ?? new();
        aggregates.IntervalsSeconds = source.GetSection("ShortAggregates:IntervalsSeconds").Get<int[]>() ?? aggregates.IntervalsSeconds;
        var features = source.GetSection("CanonicalFeatures").Get<CanonicalFeatureSettings>() ?? new();
        var patterns = source.GetSection("ForexPatterns").Get<ForexPatternSettings>() ?? new();
        patterns.ReplaceConfiguredArrays(source.GetSection("ForexPatterns"));
        var signals = source.GetSection("ForexSignals").Get<ForexSignalSettings>() ?? new();
        if (patterns.Validate().Count != 0 || signals.Validate(trading, aggregates, patterns).Count != 0)
            throw new ArgumentException("Invalid feature/pattern/signal source configuration.");
        var signal = signals.Profiles.Single(x => x.Selection.InstrumentId == instrumentId && x.Selection.StrategyId == strategyId);
        var notionalCap = Math.Min(equityUsd, 25000m);
        var risk = new RiskSettings
        {
            MaximumPositionValue = notionalCap, MaximumGrossExposure = notionalCap, MaximumNetExposure = notionalCap,
            MaximumInstrumentExposure = notionalCap, MaximumStrategyExposure = notionalCap, MaximumCorrelationGroupExposure = notionalCap,
            MaximumRiskPerTrade = Math.Min(equityUsd * .01m, 100m), MaximumDailyLoss = Math.Min(equityUsd * .03m, 300m),
            MaximumLeverage = 1, MaximumOpenPositions = 1
        };
        var costs = new ForexRiskSettings { Profiles = [new()
        {
            InstrumentId = instrumentId, StrategyId = strategyId, MaximumCurrencyExposureUsd = notionalCap,
            MaximumSnapshotAgeSeconds = 2, MaximumRulesAgeHours = 24, SlippagePerSidePips = .2m
        }] };
        var profile = new ForexResearchProfile
        {
            InstrumentId = instrumentId, StrategyId = strategyId, IntervalSeconds = signal.Selection.IntervalSeconds,
            StartingEquityUsd = equityUsd, MaximumBaseUnits = Math.Min(20000m, rules?.MaximumBaseUnits ?? 20000m),
            FillBaseUnitsPerQuote = Math.Min(20000m, rules?.MaximumBaseUnits ?? 20000m),
            FillAssumptionSource = "Hypothetical capacity capped at 20,000 EUR per callback; NOT observed executable liquidity or an actual fill.",
            LatencyMilliseconds = 500, HistoricalRules = snapshot == null ? [] : [snapshot]
        };
        var research = new ForexResearchSettings { AutomaticPaperSignalsEnabled = false, Profiles = [profile] };
        string? featureVersion = null, patternVersion = null, labelVersion = null;
        if (snapshot != null)
        {
            var contract = new ForexResearchContract(Options.Create(research), Options.Create(costs), Options.Create(risk),
                Options.Create(trading), Options.Create(patterns), Options.Create(signals), Options.Create(aggregates), Options.Create(features))
                .Get(instrumentId, strategyId);
            featureVersion = contract.Feature; patternVersion = contract.Pattern; labelVersion = contract.Label;
        }
        // These exact versions apply only to baseline signals in this separate offline configuration.
        signal.ApprovedSignalVersion = snapshot == null ? null : signal.Version;
        signal.ApprovedPatternVersion = snapshot == null ? null : patterns.Profiles.Single(x => x.InstrumentId == instrumentId
            && x.StrategyId == strategyId && x.IntervalSeconds == profile.IntervalSeconds).Version;
        signal.ApprovedFeatureVersion = featureVersion;
        var config = new
        {
            TradingSettings = trading, ForexPatterns = patterns, ForexSignals = signals, ShortAggregates = aggregates,
            CanonicalFeatures = features, ForexResearch = research, ForexRisk = costs, RiskSettings = risk,
            ForexExecution = new ForexExecutionSettings(), IbkrSettings = new { ReadOnlyApi = true },
            MarketDataCollection = new { Enabled = false }, HistoricalBackfill = new { Enabled = false },
            DatasetStorage = new { Enabled = false },
            ResearchEvaluation = new ResearchEvaluationSettings { TrainingWindowDays = 2, ValidationWindowDays = 1, StepDays = 1, HoldoutDays = 1 },
            NumericalModel = new NumericalModelSettings()
        };
        return (JsonSerializer.Serialize(config, Json), new
        {
            Scope = "Offline research only. Fictional portfolio, explicit fill assumptions, no model approval or order permission.",
            CanSendOrders = false, RulesCaptured = snapshot != null, InstrumentId = instrumentId, StrategyId = strategyId, SimulatedEquityUsd = equityUsd,
            NotionalCapUsd = notionalCap, risk.MaximumRiskPerTrade, profile.MaximumBaseUnits,
            EarliestRulesAvailableUtc = snapshot?.ObservedUtc, RulesValidThroughUtc = rules?.VerifiedAtUtc.AddHours(24),
            RulesEvidenceSha256 = rulesJson == null ? null : ForexResearchContract.Hash(rulesJson), SourceConfigSha256 = ForexResearchContract.Hash(sourceJson),
            Feature = featureVersion, Pattern = patternVersion, Label = labelVersion,
            MinimumRoundTripCommissionUsd = 4m,
            Limitations = new[] { "Without --rules this is an incomplete draft: no historical rules, signal approvals or executable replay contract.",
                "No retroactive rule timestamps. Older signals have no usable rules; future samples require refreshed evidence after expiry.",
                "100 USD standard-lot scenario is expected to reject sizes/costs; no invented odd-lot fill model.",
                "The optional larger simulated portfolio is a technical scenario, not a funding recommendation or proof of viability with 100 USD.",
                "Evaluation uses 2 training days, 1 validation day and 1 untouched holdout day; original minimum sample counts remain unchanged.",
                "Minute history does not provide bid/ask execution coverage. FX trade replay uses bounded streaming; separate feature/collection audits retain their own limits." }
        });
    }
}
