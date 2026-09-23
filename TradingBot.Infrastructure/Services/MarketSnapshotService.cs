using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class MarketSnapshotService : IMarketSnapshotService
    {
        private readonly ICandleHistoryService _candleHistory;
        private readonly IFeatureEngine _featureEngine;
        private readonly IPatternDetector _patternDetector;
        private readonly OpenAiMarketContextSettings _marketContextSettings;
        private readonly ILatestMarketDataService? _latestMarketData;

        public MarketSnapshotService(
            ICandleHistoryService candleHistory,
            IFeatureEngine featureEngine,
            IPatternDetector patternDetector,
            IOptions<OpenAiSettings>? openAiSettings = null,
            ILatestMarketDataService? latestMarketData = null)
        {
            _candleHistory = candleHistory ?? throw new ArgumentNullException(nameof(candleHistory));
            _featureEngine = featureEngine ?? throw new ArgumentNullException(nameof(featureEngine));
            _patternDetector = patternDetector ?? throw new ArgumentNullException(nameof(patternDetector));
            _marketContextSettings = openAiSettings?.Value.MarketContext ?? new OpenAiMarketContextSettings();
            _latestMarketData = latestMarketData;
        }

        public async Task<MarketSnapshot> BuildSnapshotAsync(
            string symbol,
            decimal? currentPrice = null,
            decimal? spread = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));

            var normalizedSymbol = symbol.Trim();
            var oneMinute = await BuildTimeframeSnapshotAsync(normalizedSymbol, Timeframe.OneMinute, "Entry timing", cancellationToken).ConfigureAwait(false);
            var fiveMinutes = await BuildTimeframeSnapshotAsync(normalizedSymbol, Timeframe.FiveMinutes, "Trading setup / pullback context", cancellationToken).ConfigureAwait(false);
            var fifteenMinutes = await BuildTimeframeSnapshotAsync(normalizedSymbol, Timeframe.FifteenMinutes, "Broader market direction", cancellationToken).ConfigureAwait(false);
            var latest = _latestMarketData?.Get(normalizedSymbol);
            var midpoint = latest?.Bid is decimal bid && latest.Ask is decimal ask ? (bid + ask) / 2m : (decimal?)null;

            return new MarketSnapshot
            {
                Symbol = normalizedSymbol,
                CreatedAtUtc = currentPrice.HasValue || spread.HasValue
                    ? DateTime.UtcNow
                    : latest?.AsOfUtc ?? DateTime.UtcNow,
                CurrentPrice = currentPrice ?? latest?.LastTrade ?? midpoint ?? oneMinute.RecentCandles.LastOrDefault()?.Close,
                Spread = spread ?? latest?.Spread,
                OneMinute = oneMinute,
                FiveMinutes = fiveMinutes,
                FifteenMinutes = fifteenMinutes
            };
        }

        private async Task<MarketTimeframeSnapshot> BuildTimeframeSnapshotAsync(
            string symbol,
            Timeframe timeframe,
            string interpretation,
            CancellationToken cancellationToken)
        {
            var candles = await _candleHistory.GetLastNCandlesAsync(symbol, timeframe, GetCandleCount(timeframe), cancellationToken).ConfigureAwait(false);
            if (candles.Count == 0)
            {
                return MarketTimeframeSnapshot.Empty(timeframe, interpretation);
            }

            var features = _featureEngine.ComputeFeatures(candles);
            var patterns = _patternDetector.Detect(candles);

            return new MarketTimeframeSnapshot
            {
                Timeframe = timeframe,
                Interpretation = interpretation,
                RecentCandles = candles,
                Features = features,
                DetectedPatterns = patterns,
                SupportResistanceCandidates = BuildSupportResistanceCandidates(candles, features, patterns),
                Trend = BuildTrendInformation(features),
                Volume = BuildVolumeContext(candles, features),
                Volatility = BuildVolatilityContext(features)
            };
        }

        private int GetCandleCount(Timeframe timeframe)
        {
            var configured = timeframe switch
            {
                Timeframe.OneMinute => _marketContextSettings.OneMinuteCandles,
                Timeframe.FiveMinutes => _marketContextSettings.FiveMinuteCandles,
                Timeframe.FifteenMinutes => _marketContextSettings.FifteenMinuteCandles,
                _ => _marketContextSettings.OneMinuteCandles
            };

            return Math.Clamp(configured, 1, 500);
        }

        private static IReadOnlyList<SupportResistanceCandidate> BuildSupportResistanceCandidates(
            IReadOnlyList<Candle> candles,
            MarketFeatures features,
            IReadOnlyList<PatternCandidate> patterns)
        {
            var candidates = new List<SupportResistanceCandidate>();
            var recentHigh = features.RecentHigh ?? candles.Max(c => c.High);
            var recentLow = features.RecentLow ?? candles.Min(c => c.Low);

            candidates.Add(new SupportResistanceCandidate { Price = recentLow, Kind = "Support", Source = "Recent low" });
            candidates.Add(new SupportResistanceCandidate { Price = recentHigh, Kind = "Resistance", Source = "Recent high" });

            foreach (var level in patterns.SelectMany(p => p.RelevantPriceLevels).Distinct())
            {
                candidates.Add(new SupportResistanceCandidate { Price = level, Kind = "Level", Source = "Detected pattern" });
            }

            return candidates
                .GroupBy(c => new { c.Price, c.Kind })
                .Select(g => g.First())
                .OrderBy(c => c.Price)
                .ToList();
        }

        private static TrendInformation BuildTrendInformation(MarketFeatures features)
        {
            return new TrendInformation
            {
                Direction = features.TrendDirection,
                Label = features.TrendDirection switch
                {
                    1 => "Up",
                    -1 => "Down",
                    0 => "Flat",
                    _ => "Unknown"
                },
                EmaShort = features.EmaShort,
                EmaLong = features.EmaLong,
                PriceChangePercent = features.PriceChangePercent
            };
        }

        private static VolumeContext BuildVolumeContext(IReadOnlyList<Candle> candles, MarketFeatures features)
        {
            return new VolumeContext
            {
                LatestVolume = candles[^1].Volume,
                AverageVolume = features.VolumeAverage,
                VolumeRatio = features.VolumeRatio
            };
        }

        private static VolatilityContext BuildVolatilityContext(MarketFeatures features)
        {
            return new VolatilityContext
            {
                Atr = features.Atr,
                Volatility = features.Volatility,
                RecentHigh = features.RecentHigh,
                RecentLow = features.RecentLow
            };
        }
    }
}
