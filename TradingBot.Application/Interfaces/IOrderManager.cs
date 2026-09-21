using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IOrderManager
    {
        Task<ManagedOrderResult> SubmitLimitBuyAsync(
            OrderRequest orderRequest,
            RiskDecision riskDecision,
            CancellationToken cancellationToken = default);

        Task<ManagedOrderResult> SubmitBracketOrderAsync(
            OrderRequest entryLimitBuy,
            OrderRequest stopLoss,
            OrderRequest takeProfit,
            RiskDecision riskDecision,
            CancellationToken cancellationToken = default);

        Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default);

        Task<ManagedOrderResult?> GetOrderStatusAsync(string brokerOrderId, CancellationToken cancellationToken = default);
    }
}
