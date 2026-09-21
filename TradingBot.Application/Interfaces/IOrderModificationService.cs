using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IOrderModificationService
    {
        Task<OrderStatusDto> ModifyOrderAsync(
            string brokerOrderId,
            OrderRequestDto request,
            CancellationToken cancellationToken = default);
    }
}
