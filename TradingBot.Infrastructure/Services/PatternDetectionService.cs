using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public class PatternDetectionService : IPatternDetectionService
    {
        private readonly ILogger<PatternDetectionService> _logger;

        public PatternDetectionService(ILogger<PatternDetectionService> logger)
        {
            _logger = logger;
        }

        public Task<bool> DetectPatternAsync(object marketData, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("DetectPatternAsync placeholder called");
            return Task.FromResult(false);
        }
    }
}
