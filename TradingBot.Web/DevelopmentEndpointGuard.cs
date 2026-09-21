using Microsoft.Extensions.Hosting;

namespace TradingBot.Web
{
    public static class DevelopmentEndpointGuard
    {
        public static bool ShouldExposeDevelopmentEndpoints(IHostEnvironment environment)
        {
            return environment.IsDevelopment();
        }
    }
}
