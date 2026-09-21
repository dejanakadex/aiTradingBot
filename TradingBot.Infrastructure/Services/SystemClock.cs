using System;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
