using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IAccountService
    {
        Task<AccountInfo> GetAccountInfoAsync(string accountId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Notification when account values change. Handlers should be non-blocking.
        /// </summary>
        event Func<AccountInfo, Task>? AccountUpdated;
    }
}
