namespace TradingBot.Application.Interfaces
{
    public interface IPatternDetectorFactory
    {
        string DetectorVersion { get; }
        IPatternDetector Create();
    }
}
