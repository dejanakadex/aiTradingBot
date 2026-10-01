namespace TradingBot.Persistence;

public sealed class TickCoverageGapRecord
{
    public long Id { get; set; }
    public string InstrumentId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long DroppedEvents { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}
