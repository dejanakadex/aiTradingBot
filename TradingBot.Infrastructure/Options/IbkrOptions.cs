namespace TradingBot.Infrastructure.Options
{
    public sealed class IbkrOptions
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 7496;
        public int ClientId { get; set; } = 0;
        public string? AccountId { get; set; }
        // Used by the contract-only CLI. Never requests order readiness and rejects order mutations.
        public bool ReadOnlyDiagnostics { get; set; }

        public int ReconnectDelayMs { get; set; } = 1000;
        public int MaximumReconnectDelayMs { get; set; } = 30000;
    }
}
