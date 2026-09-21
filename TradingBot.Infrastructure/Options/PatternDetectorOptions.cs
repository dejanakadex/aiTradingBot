using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Options
{
    public sealed class PatternDetectorOptions
    {
        public decimal MinimumTradeSetupQuality { get; set; } = 0.6m;
        public string[] AiAnalysisTimeframes { get; set; } = new[] { "1m" };
        public bool RejectLongWhenFifteenMinuteTrendBearish { get; set; } = true;
        public bool RejectLongWhenFiveMinuteTrendBearish { get; set; } = false;
        public int MinimumOneMinuteCandlesForTradeSetup { get; set; } = 20;
        public int MinimumFiveMinuteCandlesForTradeSetup { get; set; } = 10;
        public int MinimumFifteenMinuteCandlesForTradeSetup { get; set; } = 10;
        public decimal MaximumOneMinuteVolatilityForTradeSetup { get; set; } = 0.05m;
        public bool RequireLatestOneMinuteCandleForTradeSetup { get; set; } = true;
        public int RecentCandlesForDedup { get; set; } = 5;

        public HammerPatternOptions Hammer { get; set; } = new();
        public BullishEngulfingPatternOptions BullishEngulfing { get; set; } = new();
        public DoubleBottomPatternOptions DoubleBottom { get; set; } = new();
        public BreakoutAndRetestPatternOptions BreakoutAndRetest { get; set; } = new();
        public VwapReclaimPatternOptions VwapReclaim { get; set; } = new();

        public PatternRuleOptions ForPattern(PatternType patternType)
        {
            return patternType switch
            {
                PatternType.Hammer => Hammer,
                PatternType.BullishEngulfing => BullishEngulfing,
                PatternType.DoubleBottom => DoubleBottom,
                PatternType.BreakoutAndRetest => BreakoutAndRetest,
                PatternType.VwapReclaim => VwapReclaim,
                _ => new PatternRuleOptions()
            };
        }
    }

    public class PatternRuleOptions
    {
        public decimal MinimumQualityForTradeSetup { get; set; } = 0.6m;
        public bool RequireVolumeConfirmation { get; set; } = false;
        public decimal MinimumVolumeScore { get; set; } = 0.25m;
        public decimal MinimumConfirmationScore { get; set; } = 0.35m;
        public decimal MinimumContextScore { get; set; } = 0.25m;
    }

    public sealed class HammerPatternOptions : PatternRuleOptions
    {
        public decimal MaxBodyToRange { get; set; } = 0.3m;
        public decimal MinLowerWickToRange { get; set; } = 0.5m;
        public decimal MaxUpperWickToRange { get; set; } = 0.25m;
        public decimal MinClosePositionInRange { get; set; } = 0.6m;
        public int PullbackLookbackCandles { get; set; } = 5;
        public decimal MinPullbackPercent { get; set; } = 0.001m;
        public decimal SupportProximityPercent { get; set; } = 0.003m;
    }

    public sealed class BullishEngulfingPatternOptions : PatternRuleOptions
    {
        public decimal MinBodyRatio { get; set; } = 1.1m;
        public decimal MinFirstBodyToAtr { get; set; } = 0.15m;
        public decimal MinSecondBodyToAtr { get; set; } = 0.25m;
        public int PullbackLookbackCandles { get; set; } = 5;
        public decimal MinPullbackPercent { get; set; } = 0.001m;

        public BullishEngulfingPatternOptions()
        {
            RequireVolumeConfirmation = true;
            MinimumVolumeScore = 0.35m;
        }
    }

    public sealed class DoubleBottomPatternOptions : PatternRuleOptions
    {
        public decimal MaxLowDiffPercent { get; set; } = 0.02m;
        public int MinSeparation { get; set; } = 3;
        public int MaxSeparation { get; set; } = 40;
        public decimal MinReboundPercent { get; set; } = 0.01m;
        public bool RequireNecklineBreakForTradeSetup { get; set; } = true;
        public decimal ConfirmationTolerancePercent { get; set; } = 0.002m;

        public DoubleBottomPatternOptions()
        {
            MinimumConfirmationScore = 0.5m;
        }
    }

    public sealed class BreakoutAndRetestPatternOptions : PatternRuleOptions
    {
        public int Lookback { get; set; } = 20;
        public decimal RetestTolerancePercent { get; set; } = 0.02m;
        public decimal MinBreakoutPercent { get; set; } = 0.001m;
        public decimal MinBouncePercent { get; set; } = 0.001m;

        public BreakoutAndRetestPatternOptions()
        {
            RequireVolumeConfirmation = true;
            MinimumVolumeScore = 0.35m;
        }
    }

    public sealed class VwapReclaimPatternOptions : PatternRuleOptions
    {
        public int PriorBelowVwapCandles { get; set; } = 2;
        public decimal MinDistanceFromVwapToAtr { get; set; } = 0.15m;
        public decimal MinClosePositionInRange { get; set; } = 0.55m;

        public VwapReclaimPatternOptions()
        {
            MinimumConfirmationScore = 0.4m;
        }
    }
}
