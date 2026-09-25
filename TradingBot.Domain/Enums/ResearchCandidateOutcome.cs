namespace TradingBot.Domain.Enums
{
    public enum ResearchCandidateOutcome
    {
        Rejected = 0,
        Accepted = 1,
        Blocked = 2
    }

    public enum CandidateLabelStatus
    {
        Pending = 0,
        Complete = 1,
        InsufficientData = 2,
        Ambiguous = 3
    }

    public enum TargetStopOutcome
    {
        Neither = 0,
        TargetFirst = 1,
        StopFirst = 2,
        Ambiguous = 3
    }
}
