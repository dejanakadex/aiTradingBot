using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IOrderExecutionService
    {
        Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default);

        Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);

        Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Subscribe to order status updates and fills. Handler should be non-blocking.
        /// </summary>
        event Func<OrderStatusDto, Task>? OrderStatusUpdated;

        event Func<OrderStatusDto, Task>? OrderFilled;
    }
}
