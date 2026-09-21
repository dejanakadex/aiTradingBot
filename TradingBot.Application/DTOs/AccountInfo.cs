namespace TradingBot.Application.DTOs
{
    public sealed class AccountInfo
    {
        public string AccountId { get; init; } = string.Empty;
        public decimal NetLiquidation { get; init; }
        public decimal AvailableFunds { get; init; }
        public decimal BuyingPower { get; init; }
        public decimal MarginRequirement { get; init; }
    }
}
