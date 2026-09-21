using System;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IIbkrConnectionService
    {
        ConnectionStatus Status { get; }

        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Raised when a reconnect occurs or when connection status changes.
        /// Handlers should be resilient and non-blocking.
        /// </summary>
        event Func<ConnectionStatus, Task>? ConnectionStatusChanged;
    }
}
