using System.Collections.Generic;

namespace TradingBot.Tests.Fakes
{
    public enum FakeBrokerOrderBehavior
    {
        AcceptImmediately,
        RejectOrder,
        FillImmediately,
        PartialFill,
        NeverReturnOrderStatus,
        DisconnectBeforeSubmitResponse,
        DisconnectAfterBrokerAcceptedOrder,
        DisconnectDuringPartialFill,
        RejectForInsufficientBuyingPower,
        RejectForInvalidPrice,
        RejectBecauseMarketClosed
    }

    public sealed class FakeBrokerScenario
    {
        public FakeBrokerOrderBehavior OrderBehavior { get; init; } = FakeBrokerOrderBehavior.AcceptImmediately;
        public IReadOnlyList<decimal> FillQuantities { get; init; } = new[] { 1m };
        public IReadOnlyList<decimal> FillPrices { get; init; } = new[] { 100m };
        public decimal CommissionPerFill { get; init; }
        public string RejectionReason { get; init; } = "Fake broker rejected order.";
    }
}
