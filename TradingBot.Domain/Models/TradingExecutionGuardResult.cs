using System;
using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class TradingExecutionGuardResult
    {
        public bool Approved { get; init; }
        public string Reason { get; init; } = string.Empty;
        public IReadOnlyList<string> RejectionReasons { get; init; } = Array.Empty<string>();

        public static TradingExecutionGuardResult Approve()
        {
            return new TradingExecutionGuardResult
            {
                Approved = true,
                Reason = "Execution guard passed."
            };
        }

        public static TradingExecutionGuardResult Reject(IReadOnlyList<string> reasons)
        {
            return new TradingExecutionGuardResult
            {
                Approved = false,
                Reason = string.Join("; ", reasons),
                RejectionReasons = reasons
            };
        }
    }
}
