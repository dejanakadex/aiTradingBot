using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IPatternAnalysisDashboardService
    {
        Task<PatternAnalysisDashboard> GetAnalysisAsync(PatternAnalysisFilter filter, int take = 200, CancellationToken cancellationToken = default);
    }
}
