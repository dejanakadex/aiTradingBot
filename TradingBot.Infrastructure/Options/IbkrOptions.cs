namespace TradingBot.Infrastructure.Options
{
    public sealed class IbkrOptions
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 7496;
        public int ClientId { get; set; } = 0;
        public string? AccountId { get; set; }

        public int ReconnectDelayMs { get; set; } = 1000;
        public int MaximumReconnectDelayMs { get; set; } = 30000;
    }
}
