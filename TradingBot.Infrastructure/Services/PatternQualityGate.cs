using System.Globalization;
using Microsoft.Extensions.Options;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    public sealed class PatternQualityGate : IPatternQualityGate
    {
        private readonly PatternDetectorOptions _options;

        public PatternQualityGate(IOptions<PatternDetectorOptions> options)
        {
            _options = options?.Value ?? new PatternDetectorOptions();
        }

        public bool TryCreateTradeSetup(
            PatternCandidate pattern,
            MarketSnapshot snapshot,
            out TradeSetupCandidate setupCandidate,
            out IReadOnlyList<string> rejectionReasons)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var reasons = new List<string>();
            var breakdown = BuildBreakdown(pattern);
            var rule = _options.ForPattern(pattern.PatternType);
            var minimumQuality = Math.Max(_options.MinimumTradeSetupQuality, rule.MinimumQualityForTradeSetup);

            if (!IsConfiguredAiTimeframe(pattern.Timeframe))
            {
                reasons.Add($"Pattern timeframe {pattern.Timeframe} is not configured as an AI entry trigger.");
            }

            if (pattern.Confidence < minimumQuality)
            {
                reasons.Add($"Pattern final quality {pattern.Confidence:0.####} is below trade setup threshold {minimumQuality:0.####}.");
            }

            if (breakdown.ConfirmationScore is decimal confirmation && confirmation < rule.MinimumConfirmationScore)
            {
                reasons.Add($"Pattern confirmation score {confirmation:0.####} is below minimum {rule.MinimumConfirmationScore:0.####}.");
            }

            if (rule.RequireVolumeConfirmation
                && breakdown.VolumeScore is decimal volume
                && volume < rule.MinimumVolumeScore)
            {
                reasons.Add($"Pattern volume score {volume:0.####} is below minimum {rule.MinimumVolumeScore:0.####}.");
            }

            if (breakdown.ContextScore is decimal context && context < rule.MinimumContextScore)
            {
                reasons.Add($"Pattern immediate context score {context:0.####} is below minimum {rule.MinimumContextScore:0.####}.");
            }

            ValidateSnapshotContext(pattern, snapshot, reasons);
            ValidatePatternSpecificRules(pattern, breakdown, reasons);

            setupCandidate = new TradeSetupCandidate(pattern, breakdown, reasons);
            rejectionReasons = reasons;
            return reasons.Count == 0;
        }

        private void ValidateSnapshotContext(PatternCandidate pattern, MarketSnapshot snapshot, List<string> reasons)
        {
            if (snapshot.OneMinute.RecentCandles.Count < _options.MinimumOneMinuteCandlesForTradeSetup)
            {
                reasons.Add($"Insufficient 1m context: {snapshot.OneMinute.RecentCandles.Count} candles available.");
            }

            if (snapshot.FiveMinutes.RecentCandles.Count < _options.MinimumFiveMinuteCandlesForTradeSetup)
            {
                reasons.Add($"Insufficient 5m context: {snapshot.FiveMinutes.RecentCandles.Count} candles available.");
            }

            if (snapshot.FifteenMinutes.RecentCandles.Count < _options.MinimumFifteenMinuteCandlesForTradeSetup)
            {
                reasons.Add($"Insufficient 15m context: {snapshot.FifteenMinutes.RecentCandles.Count} candles available.");
            }

            var latestOneMinute = snapshot.OneMinute.RecentCandles.OrderByDescending(c => c.TimestampUtc).FirstOrDefault();
            if (_options.RequireLatestOneMinuteCandleForTradeSetup
                && pattern.Timeframe == Timeframe.OneMinute
                && latestOneMinute != null
                && latestOneMinute.TimestampUtc != pattern.DetectedAtUtc)
            {
                reasons.Add("Pattern is not tied to the latest completed 1m candle.");
            }

            if (_options.RejectLongWhenFifteenMinuteTrendBearish
                && snapshot.FifteenMinutes.Trend.Direction == -1)
            {
                reasons.Add("15m trend is bearish for this long setup strategy.");
            }

            if (_options.RejectLongWhenFiveMinuteTrendBearish
                && snapshot.FiveMinutes.Trend.Direction == -1)
            {
                reasons.Add("5m trend is bearish for this long setup strategy.");
            }

            if (snapshot.OneMinute.Volatility.Volatility is decimal volatility
                && volatility > _options.MaximumOneMinuteVolatilityForTradeSetup)
            {
                reasons.Add($"1m volatility {volatility:0.####} exceeds pattern gate maximum {_options.MaximumOneMinuteVolatilityForTradeSetup:0.####}.");
            }
        }

        private void ValidatePatternSpecificRules(PatternCandidate pattern, PatternQualityBreakdown breakdown, List<string> reasons)
        {
            if (pattern.PatternType == PatternType.DoubleBottom
                && _options.DoubleBottom.RequireNecklineBreakForTradeSetup
                && (!pattern.Metadata.TryGetValue("necklineBreak", out var necklineBreak)
                    || !bool.TryParse(necklineBreak, out var brokeNeckline)
                    || !brokeNeckline))
            {
                reasons.Add("Double Bottom has no neckline break confirmation.");
            }

            if (pattern.PatternType == PatternType.VwapReclaim
                && breakdown.ConfirmationScore is decimal confirmation
                && confirmation < _options.VwapReclaim.MinimumConfirmationScore)
            {
                reasons.Add("VWAP Reclaim confirmation is too weak.");
            }
        }

        private bool IsConfiguredAiTimeframe(Timeframe timeframe)
        {
            var configured = _options.AiAnalysisTimeframes ?? Array.Empty<string>();
            if (configured.Length == 0) return timeframe == Timeframe.OneMinute;

            return configured.Any(value => ToTimeframe(value) == timeframe);
        }

        private static Timeframe? ToTimeframe(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "1m" or "1 min" or "oneminute" or "oneminute" => Timeframe.OneMinute,
                "5m" or "5 mins" or "fiveminutes" or "fiveminute" => Timeframe.FiveMinutes,
                "15m" or "15 mins" or "fifteenminutes" or "fifteenminute" => Timeframe.FifteenMinutes,
                _ => null
            };
        }

        private static PatternQualityBreakdown BuildBreakdown(PatternCandidate pattern)
        {
            return new PatternQualityBreakdown
            {
                GeometryScore = GetScore(pattern, "geometryScore"),
                ContextScore = GetScore(pattern, "contextScore"),
                ConfirmationScore = GetScore(pattern, "confirmationScore"),
                VolumeScore = GetScore(pattern, "volumeScore"),
                LocationScore = GetScore(pattern, "locationScore"),
                FinalQuality = GetScore(pattern, "finalQuality") ?? pattern.Confidence,
                Details = new Dictionary<string, string>(pattern.Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static decimal? GetScore(PatternCandidate pattern, string key)
        {
            if (!pattern.Metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, 0m, 1m)
                : null;
        }
    }
}
