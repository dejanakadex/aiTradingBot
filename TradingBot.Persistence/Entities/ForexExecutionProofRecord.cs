namespace TradingBot.Persistence;

/// <summary>Durable FX risk input and result tied to the existing atomic reservation.</summary>
public sealed class ForexExecutionProofRecord
{
    public Guid ReservationId { get; set; }
    public string InstrumentId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string RequestJson { get; set; } = "";
    public string AssessmentJson { get; set; } = "";
}
