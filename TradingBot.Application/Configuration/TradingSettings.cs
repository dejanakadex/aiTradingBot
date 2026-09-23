namespace TradingBot.Application.Configuration
{
    using TradingBot.Domain.Enums;
    using TradingBot.Domain.Models;

    public class TradingSettings
    {
        public bool Enabled { get; set; } = false;
        public TradingOperatingMode OperatingMode { get; set; } = TradingOperatingMode.AnalysisOnly;
        public bool LiveTradingExplicitlyEnabled { get; set; } = false;
        public string[] Symbols { get; set; } = new[] { "SPY" };
        public InstrumentSettings[] Instruments { get; set; } = Array.Empty<InstrumentSettings>();
        public string[] MarketDataTimeframes { get; set; } = new[] { "1m", "5m", "15m" };
        public double MinimumPatternQualityForAiAnalysis { get; set; } = 0.6d;
        public decimal MinimumAiConfidence { get; set; } = 0.6m;
        public decimal MinimumPatternQuality { get; set; } = 0.6m;
        public decimal MinimumRewardRiskRatio { get; set; } = 2.0m;
        public decimal MaximumSpread { get; set; } = 0.05m;
        public string[] AllowedMarketRegimes { get; set; } = new[] { "pullback in uptrend", "uptrend", "trending" };
        public int TradingStartHourUtc { get; set; } = 13;
        public int TradingEndHourUtc { get; set; } = 21;
        public int TradingStartHourNewYork { get; set; } = 9;
        public int TradingStartMinuteNewYork { get; set; } = 30;
        public int TradingEndHourNewYork { get; set; } = 16;
        public int TradingEndMinuteNewYork { get; set; } = 0;
        public int MaximumQuoteAgeSeconds { get; set; } = 120;
        public int MaximumCandleAgeSeconds { get; set; } = 300;
        public decimal MaximumAiEntryDeviationPercent { get; set; } = 10m;
        public decimal ExtremeVolatilityPercent { get; set; } = 0.05m;
        public bool RequireProtectiveStopForBotPositions { get; set; } = true;
        public int ProtectiveStopMonitorIntervalSeconds { get; set; } = 15;

        public IReadOnlyList<ConfiguredInstrument> GetEnabledInstruments()
        {
            if (Instruments?.Length > 0)
            {
                return Instruments
                    .Where(instrument => instrument != null && instrument.Enabled)
                    .Select(ToConfiguredInstrument)
                    .ToArray();
            }

            return (Symbols ?? Array.Empty<string>())
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .Select(symbol => symbol.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(symbol => new ConfiguredInstrument(
                    symbol,
                    symbol,
                    "SMART",
                    "USD",
                    "STK",
                    true,
                    new[] { TradeDirection.Long },
                    new[] { PipelineContractVersions.DefaultStrategyId },
                    NormalizeTimeframes(MarketDataTimeframes),
                    null,
                    null))
                .ToArray();
        }

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            var configured = Instruments?.Length > 0;

            if (!configured)
            {
                if (Symbols?.Any(symbol => !string.IsNullOrWhiteSpace(symbol)) != true)
                {
                    errors.Add("TradingSettings must contain at least one instrument or legacy symbol.");
                }

                if (NormalizeTimeframes(MarketDataTimeframes).Count == 0)
                {
                    errors.Add("TradingSettings:MarketDataTimeframes must contain at least one supported timeframe.");
                }

                return errors;
            }

            var instrumentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var instrument in Instruments!)
            {
                if (instrument == null)
                {
                    errors.Add("TradingSettings:Instruments cannot contain null entries.");
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(instrument.InstrumentId) ? "<missing>" : instrument.InstrumentId.Trim();
                if (string.IsNullOrWhiteSpace(instrument.InstrumentId))
                {
                    errors.Add("Every instrument requires InstrumentId.");
                }
                else if (!instrumentIds.Add(label))
                {
                    errors.Add($"Duplicate InstrumentId '{label}'.");
                }

                if (string.IsNullOrWhiteSpace(instrument.Symbol))
                {
                    errors.Add($"Instrument '{label}' requires Symbol.");
                }
                else if (!symbols.Add(instrument.Symbol.Trim()))
                {
                    errors.Add($"Duplicate Symbol '{instrument.Symbol.Trim()}' is not supported by the current symbol-based broker adapter.");
                }

                if (string.IsNullOrWhiteSpace(instrument.Exchange)) errors.Add($"Instrument '{label}' requires Exchange.");
                if (string.IsNullOrWhiteSpace(instrument.Currency)) errors.Add($"Instrument '{label}' requires Currency.");
                if (string.IsNullOrWhiteSpace(instrument.SecurityType)) errors.Add($"Instrument '{label}' requires SecurityType.");
                if (instrument.TradingEnabled && !instrument.Enabled) errors.Add($"Instrument '{label}' cannot enable trading while disabled.");
                if (instrument.AllowedDirections is not { Length: > 0 }) errors.Add($"Instrument '{label}' requires at least one allowed direction.");
                if (instrument.AllowedDirections?.Any(direction => !Enum.IsDefined(direction)) == true) errors.Add($"Instrument '{label}' contains an unsupported trade direction.");
                if (NormalizeValues(instrument.StrategyIds).Count == 0) errors.Add($"Instrument '{label}' requires at least one StrategyId.");
                if (NormalizeValues(instrument.StrategyIds).Count != (instrument.StrategyIds?.Count(value => !string.IsNullOrWhiteSpace(value)) ?? 0)) errors.Add($"Instrument '{label}' contains duplicate StrategyIds.");
                if (instrument.MaximumPositionValue is <= 0m) errors.Add($"Instrument '{label}' MaximumPositionValue must be greater than zero when configured.");
                if (instrument.MaximumHoldingSeconds is <= 0) errors.Add($"Instrument '{label}' MaximumHoldingSeconds must be greater than zero when configured.");

                var configuredTimeframes = instrument.MarketDataTimeframes?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
                    ? instrument.MarketDataTimeframes
                    : MarketDataTimeframes;
                var unsupportedTimeframes = (configuredTimeframes ?? Array.Empty<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value) && NormalizeTimeframe(value).Length == 0)
                    .ToArray();
                if (unsupportedTimeframes.Length > 0)
                {
                    errors.Add($"Instrument '{label}' contains unsupported market-data timeframes: {string.Join(", ", unsupportedTimeframes)}.");
                }
                if (NormalizeTimeframes(configuredTimeframes).Count == 0)
                {
                    errors.Add($"Instrument '{label}' requires at least one supported market-data timeframe.");
                }
            }

            if (!Instruments!.Any(instrument => instrument?.Enabled == true))
            {
                errors.Add("TradingSettings:Instruments must contain at least one enabled instrument.");
            }

            return errors;
        }

        private ConfiguredInstrument ToConfiguredInstrument(InstrumentSettings instrument)
        {
            var timeframes = instrument.MarketDataTimeframes?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
                ? instrument.MarketDataTimeframes
                : MarketDataTimeframes;

            return new ConfiguredInstrument(
                instrument.InstrumentId.Trim(),
                instrument.Symbol.Trim().ToUpperInvariant(),
                instrument.Exchange.Trim().ToUpperInvariant(),
                instrument.Currency.Trim().ToUpperInvariant(),
                instrument.SecurityType.Trim().ToUpperInvariant(),
                instrument.TradingEnabled,
                (instrument.AllowedDirections ?? Array.Empty<TradeDirection>()).Distinct().ToArray(),
                NormalizeValues(instrument.StrategyIds),
                NormalizeTimeframes(timeframes),
                instrument.MaximumPositionValue,
                instrument.MaximumHoldingSeconds);
        }

        private static IReadOnlyList<string> NormalizeValues(IEnumerable<string>? values)
        {
            return (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<string> NormalizeTimeframes(IEnumerable<string>? values)
        {
            return (values ?? Array.Empty<string>())
                .Select(NormalizeTimeframe)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeTimeframe(string? timeframe)
        {
            return timeframe?.Trim().ToLowerInvariant() switch
            {
                "1m" or "1 min" or "1 minute" => "1m",
                "5m" or "5 mins" or "5 minutes" => "5m",
                "15m" or "15 mins" or "15 minutes" => "15m",
                _ => string.Empty
            };
        }
    }
}
