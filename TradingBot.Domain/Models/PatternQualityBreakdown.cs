using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class PatternQualityBreakdown
    {
        public decimal? GeometryScore { get; init; }
        public decimal? ContextScore { get; init; }
        public decimal? ConfirmationScore { get; init; }
        public decimal? VolumeScore { get; init; }
        public decimal? LocationScore { get; init; }
        public decimal FinalQuality { get; init; }
        public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
    }
}
