using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Persistence;

namespace TradingBot.Web.HealthChecks
{
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        private readonly IDbContextFactory<TradingBotDbContext> _dbContextFactory;

        public DatabaseHealthCheck(IDbContextFactory<TradingBotDbContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                var canConnect = await db.Database.CanConnectAsync(cancellationToken);
                return canConnect
                    ? HealthCheckResult.Healthy("SQLite database is reachable.")
                    : HealthCheckResult.Unhealthy("SQLite database is not reachable.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("SQLite database health check failed.", ex);
            }
        }
    }
}
