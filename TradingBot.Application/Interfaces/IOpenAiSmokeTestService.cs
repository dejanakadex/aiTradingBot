using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IOpenAiSmokeTestService
    {
        Task<OpenAiSmokeTestResult> RunAsync(CancellationToken cancellationToken = default);
    }
}
