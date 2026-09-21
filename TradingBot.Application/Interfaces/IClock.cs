using System;

namespace TradingBot.Application.Interfaces
{
    public interface IClock
    {
        DateTime UtcNow { get; }
    }
}
