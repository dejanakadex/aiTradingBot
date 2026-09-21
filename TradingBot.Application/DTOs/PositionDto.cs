namespace TradingBot.Application.DTOs
{
    public sealed class PositionDto
    {
        public string AccountId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public decimal AveragePrice { get; init; }
    }
}
