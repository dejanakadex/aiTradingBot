using System;
using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class AiTradeCriticResult
    {
        public bool Approved { get; init; }
        public decimal Confidence { get; init; }
        public IReadOnlyList<string> RiskFlags { get; init; } = Array.Empty<string>();
        public string Reason { get; init; } = string.Empty;
        public DateTime CritiquedAtUtc { get; init; } = DateTime.UtcNow;
        public string Model { get; init; } = string.Empty;
        public bool IsSafeFallback { get; init; }

        public static AiTradeCriticResult Reject(string reason, string model = "")
        {
            return new AiTradeCriticResult
            {
                Approved = false,
                Confidence = 1m,
                RiskFlags = new[] { "AI_CRITIC_UNAVAILABLE" },
                Reason = reason,
                Model = model,
                IsSafeFallback = true
            };
        }
    }
}
