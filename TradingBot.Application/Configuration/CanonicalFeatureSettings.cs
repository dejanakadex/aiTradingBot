namespace TradingBot.Application.Configuration
{
    public sealed class CanonicalFeatureSettings
    {
        public int EmaShortPeriod { get; set; } = 12;
        public int EmaLongPeriod { get; set; } = 26;
        public int RsiPeriod { get; set; } = 14;
        public int AtrPeriod { get; set; } = 14;
        public int VolumeAveragePeriod { get; set; } = 20;
        public int MomentumLookbackCandles { get; set; } = 5;
        public int MeanReversionLookbackCandles { get; set; } = 20;
        public int MaximumQuoteAgeSeconds { get; set; } = 30;
        public decimal TrendingEmaSeparationAtrRatio { get; set; } = 0.25m;
        public decimal VolatileAtrToPriceRatio { get; set; } = 0.02m;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (EmaShortPeriod <= 0) errors.Add("CanonicalFeatures:EmaShortPeriod must be greater than zero.");
            if (EmaLongPeriod <= EmaShortPeriod) errors.Add("CanonicalFeatures:EmaLongPeriod must be greater than EmaShortPeriod.");
            if (RsiPeriod <= 0) errors.Add("CanonicalFeatures:RsiPeriod must be greater than zero.");
            if (AtrPeriod <= 0) errors.Add("CanonicalFeatures:AtrPeriod must be greater than zero.");
            if (VolumeAveragePeriod <= 0) errors.Add("CanonicalFeatures:VolumeAveragePeriod must be greater than zero.");
            if (MomentumLookbackCandles <= 0) errors.Add("CanonicalFeatures:MomentumLookbackCandles must be greater than zero.");
            if (MeanReversionLookbackCandles < 2) errors.Add("CanonicalFeatures:MeanReversionLookbackCandles must be at least 2.");
            if (MaximumQuoteAgeSeconds < 0) errors.Add("CanonicalFeatures:MaximumQuoteAgeSeconds cannot be negative.");
            if (TrendingEmaSeparationAtrRatio < 0m) errors.Add("CanonicalFeatures:TrendingEmaSeparationAtrRatio cannot be negative.");
            if (VolatileAtrToPriceRatio <= 0m) errors.Add("CanonicalFeatures:VolatileAtrToPriceRatio must be greater than zero.");
            return errors;
        }
    }
}
