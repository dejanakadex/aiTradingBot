using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Application.Interfaces
{
    public interface IRiskManagementService
    {
        Task<bool> AssessRiskAsync(object proposedOrder, CancellationToken cancellationToken = default);
    }
}
