using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public class OrderExecutionService : IOrderExecutionService
    {
        private readonly ILogger<OrderExecutionService> _logger;

        public OrderExecutionService(ILogger<OrderExecutionService> logger)
        {
            _logger = logger;
        }

#pragma warning disable CS0067 // Event is part of the broker execution abstraction contract.
        public event System.Func<OrderStatusDto, Task>? OrderStatusUpdated;
        public event System.Func<OrderStatusDto, Task>? OrderFilled;
#pragma warning restore CS0067

        public Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Order submission rejected because no concrete broker execution adapter is registered for {symbol}", request?.Symbol);
            var dto = new OrderStatusDto
            {
                OrderId = string.Empty,
                Status = "Rejected",
                BrokerOrderId = null,
                TimestampUtc = System.DateTime.UtcNow,
                Message = "No concrete broker execution adapter is registered."
            };
            return Task.FromResult(dto);
        }

        public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Cancel order rejected because no concrete broker execution adapter is registered for {orderId}", orderId);
            return Task.FromResult(false);
        }

        public Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("GetOpenOrdersAsync placeholder called");
            return Task.FromResult<IEnumerable<OrderStatusDto>>(new List<OrderStatusDto>());
        }
    }
}
