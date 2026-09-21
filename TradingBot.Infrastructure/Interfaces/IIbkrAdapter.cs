using System;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;

namespace TradingBot.Infrastructure.Interfaces
{
    // Adapter boundary for a concrete IBKR implementation that may reference IB API types.
    // This interface must be implemented inside Infrastructure by a class that references the IB API package.
    public interface IIbkrAdapter
    {
        bool IsConnected { get; }

        /// <summary>
        /// Start the adapter and connect to the broker.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Stop the adapter and disconnect.
        /// </summary>
        Task StopAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Notifies when the underlying connection status changes.
        /// </summary>
        event Func<ConnectionStatus, Task>? ConnectionStatusChanged;

        /// <summary>
        /// Notifies when the adapter reports readiness (e.g., account info available).
        /// </summary>
        event Func<bool, Task>? ReadinessChanged;

        /// <summary>
        /// Emitted when a market bar (completed bar) is received from the broker.
        /// Implementations should emit MarketBar instances for completed candles only.
        /// </summary>
        event Func<TradingBot.Application.DTOs.MarketBar, Task>? MarketBarReceived;
    }
}
