using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Application.Interfaces
{
    public interface IPatternDetectionService
    {
        Task<bool> DetectPatternAsync(object marketData, CancellationToken cancellationToken = default);
    }
}
