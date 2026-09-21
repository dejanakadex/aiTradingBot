using System;

namespace TradingBot.Persistence
{
    public class BotSession
    {
        public int Id { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? StoppedUtc { get; set; }
        public string Notes { get; set; } = string.Empty;
    }
}
