using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class TradingControlStateRecord
    {
        public int Id { get; set; }
        public TradingPermissionState State { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
        public long Version { get; set; }
    }
}
