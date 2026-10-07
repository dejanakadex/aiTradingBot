using TradingBot.Domain.Enums;

namespace TradingBot.Persistence;

public sealed class NumericalModelDecisionRecord
{
    public long Id { get; set; }
    public Guid ModelId { get; set; }
    public string InstrumentId { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public NumericalModelStatus StatusBefore { get; set; }
    public NumericalModelStatus StatusAfter { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reviewer { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ReviewedOutputSha256 { get; set; } = string.Empty;
    public DateTime DecidedAtUtc { get; set; }
}
