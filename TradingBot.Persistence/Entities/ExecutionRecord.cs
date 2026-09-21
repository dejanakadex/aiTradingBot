using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public class ExecutionRecord
    {
        public int Id { get; set; }
        public string BrokerExecutionId { get; set; } = string.Empty;
        public int OrderRecordId { get; set; }
        public ExecutionStatus Status { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string RawJson { get; set; } = string.Empty;
    }
}
