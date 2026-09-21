using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public class RiskManagementService : IRiskManagementService
    {
        private readonly ILogger<RiskManagementService> _logger;

        public RiskManagementService(ILogger<RiskManagementService> logger)
        {
            _logger = logger;
        }

        public Task<bool> AssessRiskAsync(object proposedOrder, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("AssessRiskAsync placeholder called");
            return Task.FromResult(false);
        }
    }
}
