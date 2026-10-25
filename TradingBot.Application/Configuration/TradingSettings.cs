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
            return GetConfiguredInstruments()
                .Where(instrument => instrument.Enabled)
                .ToArray();
        }

        public IReadOnlyList<ConfiguredInstrument> GetConfiguredInstruments()
        {
            if (Instruments?.Length > 0)
            {
                return Instruments
                    .Where(instrument => instrument != null)
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
            var contracts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                ValidateInstrumentType(instrument, label, errors);
                var symbol = NormalizeSymbol(instrument);
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    errors.Add($"Instrument '{label}' requires Symbol.");
                }
                else if (!symbols.Add(symbol))
                {
                    errors.Add($"Duplicate Symbol '{symbol}' is not supported by the current symbol-based pipeline. Use a unique canonical symbol per instrument.");
                }
                var contract = InstrumentIdentity.ContractKey(instrument.SecurityType, symbol, instrument.Exchange, instrument.Currency);
                if (!contracts.Add(contract)) errors.Add($"Duplicate contract '{contract}' under instrument '{label}'.");

                if (string.IsNullOrWhiteSpace(instrument.Exchange)) errors.Add($"Instrument '{label}' requires Exchange.");
                if (string.IsNullOrWhiteSpace(instrument.Currency)) errors.Add($"Instrument '{label}' requires Currency.");
                if (string.IsNullOrWhiteSpace(instrument.SecurityType)) errors.Add($"Instrument '{label}' requires SecurityType.");
                if (instrument.TradingEnabled && !instrument.Enabled) errors.Add($"Instrument '{label}' cannot enable trading while disabled.");
                if (instrument.AllowedDirections is { Length: 0 }) errors.Add($"Instrument '{label}' requires at least one allowed direction.");
                if (instrument.AllowedDirections?.Any(direction => !Enum.IsDefined(direction)) == true) errors.Add($"Instrument '{label}' contains an unsupported trade direction.");
                var strategyIds = GetStrategyIds(instrument);
                if (NormalizeValues(strategyIds).Count == 0) errors.Add($"Instrument '{label}' requires at least one StrategyId.");
                if (NormalizeValues(strategyIds).Count != strategyIds.Count(value => !string.IsNullOrWhiteSpace(value))) errors.Add($"Instrument '{label}' contains duplicate StrategyIds.");
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
            // Also protect non-web callers from accidentally passing CASH into the current STK broker path.
            var errors = new List<string>();
            ValidateInstrumentType(instrument, instrument.InstrumentId, errors);
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
            var timeframes = instrument.MarketDataTimeframes?.Any(value => !string.IsNullOrWhiteSpace(value)) == true
                ? instrument.MarketDataTimeframes
                : MarketDataTimeframes;

            return new ConfiguredInstrument(
                instrument.InstrumentId.Trim(),
                NormalizeSymbol(instrument),
                instrument.Exchange.Trim().ToUpperInvariant(),
                instrument.Currency.Trim().ToUpperInvariant(),
                instrument.SecurityType.Trim().ToUpperInvariant(),
                instrument.Enabled,
                instrument.TradingEnabled,
                (instrument.AllowedDirections ?? new[] { TradeDirection.Long }).Distinct().ToArray(),
                NormalizeValues(GetStrategyIds(instrument)),
                NormalizeTimeframes(timeframes),
                instrument.MaximumPositionValue,
                instrument.MaximumHoldingSeconds);
        }

        private static string NormalizeSymbol(InstrumentSettings instrument)
        {
            if (InstrumentIdentity.GetInstrumentType(instrument.SecurityType) != InstrumentType.Forex)
                return InstrumentIdentity.Normalize(instrument.Symbol);
            return string.IsNullOrWhiteSpace(instrument.Symbol)
                ? InstrumentIdentity.Normalize(instrument.BaseCurrency) + InstrumentIdentity.Normalize(instrument.Currency)
                : InstrumentIdentity.NormalizeForexSymbol(instrument.Symbol);
        }

        private static string[] GetStrategyIds(InstrumentSettings instrument) => instrument.StrategyIds
            ?? (InstrumentIdentity.GetInstrumentType(instrument.SecurityType) == InstrumentType.Forex
                ? Array.Empty<string>() : new[] { PipelineContractVersions.DefaultStrategyId });

        private static void ValidateInstrumentType(InstrumentSettings instrument, string label, List<string> errors)
        {
            var type = InstrumentIdentity.GetInstrumentType(instrument.SecurityType);
            if (type == InstrumentType.Unknown)
            {
                errors.Add($"Instrument '{label}' has unsupported SecurityType; use STK or CASH.");
                return;
            }
            if (type == InstrumentType.Stock)
            {
                if (!string.IsNullOrWhiteSpace(instrument.BaseCurrency))
                    errors.Add($"Stock instrument '{label}' must not specify forex BaseCurrency.");
                return;
            }

            var basis = InstrumentIdentity.Normalize(instrument.BaseCurrency);
            var quote = InstrumentIdentity.Normalize(instrument.Currency);
            if (NormalizeValues(instrument.StrategyIds).Count == 0)
                errors.Add($"Forex instrument '{label}' requires explicit StrategyIds; stock strategy defaults are not inherited.");
            if (!InstrumentIdentity.IsCurrencyCode(basis)) errors.Add($"Forex instrument '{label}' requires a three-letter BaseCurrency.");
            if (!InstrumentIdentity.IsCurrencyCode(quote)) errors.Add($"Forex instrument '{label}' requires a three-letter quote Currency.");
            if (basis == quote) errors.Add($"Forex instrument '{label}' requires different base and quote currencies.");
            if (NormalizeSymbol(instrument) != basis + quote)
                errors.Add($"Forex instrument '{label}' Symbol must match BaseCurrency + Currency ('{basis}{quote}'); omit Symbol to derive it.");
            if (InstrumentIdentity.Normalize(instrument.Exchange) != "IDEALPRO")
                errors.Add($"Forex instrument '{label}' requires Exchange=IDEALPRO.");
            if (instrument.TradingEnabled)
                errors.Add($"Forex instrument '{label}' must keep TradingEnabled=false until FX execution is implemented (FX-8/FX-9). Collection may be enabled independently.");
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
