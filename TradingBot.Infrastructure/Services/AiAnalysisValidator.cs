using System;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class AiAnalysisValidator : IAiAnalysisValidator
    {
        private readonly TradingSettings _settings;

        public AiAnalysisValidator(IOptions<TradingSettings> settings)
        {
            _settings = settings.Value;
        }

        public bool TryValidate(MarketSnapshot snapshot, AiMarketAnalysisResult analysis, out string reason)
        {
            reason = string.Empty;
            if (analysis.Action is not (AiMarketActions.Buy or AiMarketActions.Wait or AiMarketActions.Reject))
            {
                reason = "Invalid AI action.";
                return false;
            }

            if (analysis.Confidence < 0m || analysis.Confidence > 1m || analysis.PatternQuality < 0m || analysis.PatternQuality > 1m)
            {
                reason = "AI confidence or pattern quality is outside 0..1.";
                return false;
            }

            if (analysis.EntryMin > analysis.EntryMax)
            {
                reason = "AI entryMin is greater than entryMax.";
                return false;
            }

            if (string.Equals(analysis.Action, AiMarketActions.Buy, StringComparison.OrdinalIgnoreCase))
            {
                if (analysis.EntryMin <= 0m || analysis.EntryMax <= 0m)
                {
                    reason = "BUY analysis contains non-positive entry price.";
                    return false;
                }

                if (analysis.InvalidationPrice <= 0m || analysis.InvalidationPrice >= analysis.EntryMin)
                {
                    reason = "BUY analysis stop/invalidation is not below entry.";
                    return false;
                }

                if (analysis.ExpectedMovePercent < 0m || analysis.ExpectedHorizonMinutes <= 0)
                {
                    reason = "BUY analysis has invalid expected move or horizon.";
                    return false;
                }

                if (snapshot.CurrentPrice is decimal current && current > 0m)
                {
                    var midpoint = (analysis.EntryMin + analysis.EntryMax) / 2m;
                    var deviation = Math.Abs(midpoint - current) / current * 100m;
                    if (deviation > _settings.MaximumAiEntryDeviationPercent)
                    {
                        reason = $"AI entry zone deviates {deviation:0.####}% from current price, above maximum {_settings.MaximumAiEntryDeviationPercent:0.####}%.";
                        return false;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(analysis.Reason))
            {
                reason = "AI reason is required.";
                return false;
            }

            return true;
        }
    }
}
