namespace TradingBot.Persistence
{
    public sealed class ResearchCalibrationApprovalRecord
    {
        public long Id { get; set; }
        public Guid CalibrationProfileId { get; set; }
        public ResearchCalibrationProfileRecord CalibrationProfile { get; set; } = null!;
        public int Revision { get; set; }
        public string Action { get; set; } = string.Empty;
        public string Reviewer { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string ExpectedOutputSha256 { get; set; } = string.Empty;
        public Guid? RelatedProfileId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
