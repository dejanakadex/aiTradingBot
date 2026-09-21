namespace TradingBot.Application.Configuration
{
    using TradingBot.Domain.Enums;

    public class IbkrSettings
    {
        public string Host { get; set; } = "localhost";
        public IbkrHostType HostType { get; set; } = IbkrHostType.Gateway;
        public int ClientId { get; set; } = 1;
        public string? AccountId { get; set; }
        public string? PaperAccountId { get; set; }
        public IbkrEndpointSettings Paper { get; set; } = new() { Port = 4002 };
        public IbkrEndpointSettings Live { get; set; } = new() { Port = 4001 };

        public int GetPort(TradingOperatingMode mode)
        {
            return mode == TradingOperatingMode.LiveTrading ? Live.Port : Paper.Port;
        }
    }
}
