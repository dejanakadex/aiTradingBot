using System;
using System.Collections.Generic;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class RiskDecision
    {
        public RiskDecisionType Decision { get; }
        public string Reason { get; }
        public DateTime DecidedAtUtc { get; }
        public decimal ApprovedQuantity { get; }
        public decimal PositionValue { get; }
        public decimal RiskAmount { get; }
        public IReadOnlyList<string> RejectionReasons { get; }

        public RiskDecision(RiskDecisionType decision, string reason, decimal approvedQuantity, DateTime decidedAtUtc)
        {
            if (approvedQuantity < 0m) throw new ArgumentOutOfRangeException(nameof(approvedQuantity));

            Decision = decision;
            Reason = reason ?? string.Empty;
            DecidedAtUtc = decidedAtUtc.Kind == DateTimeKind.Utc ? decidedAtUtc : decidedAtUtc.ToUniversalTime();
            ApprovedQuantity = approvedQuantity;
            PositionValue = 0m;
            RiskAmount = 0m;
            RejectionReasons = Array.Empty<string>();
        }

        public RiskDecision(RiskDecisionType decision, string reason, decimal approvedQuantity, decimal positionValue, decimal riskAmount, DateTime decidedAtUtc, IReadOnlyList<string>? rejectionReasons = null)
        {
            if (approvedQuantity < 0m) throw new ArgumentOutOfRangeException(nameof(approvedQuantity));
            if (positionValue < 0m) throw new ArgumentOutOfRangeException(nameof(positionValue));
            if (riskAmount < 0m) throw new ArgumentOutOfRangeException(nameof(riskAmount));

            Decision = decision;
            Reason = reason ?? string.Empty;
            ApprovedQuantity = approvedQuantity;
            PositionValue = positionValue;
            RiskAmount = riskAmount;
            DecidedAtUtc = decidedAtUtc.Kind == DateTimeKind.Utc ? decidedAtUtc : decidedAtUtc.ToUniversalTime();
            RejectionReasons = rejectionReasons ?? Array.Empty<string>();
        }
    }
}
