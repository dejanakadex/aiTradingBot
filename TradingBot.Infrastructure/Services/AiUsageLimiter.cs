using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public sealed class AiUsageLimiter : IAiUsageLimiter
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly OpenAiSettings _settings;
        private readonly IClock _clock;
        private readonly ILogger<AiUsageLimiter> _logger;

        public AiUsageLimiter(
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IOptions<OpenAiSettings> settings,
            IClock clock,
            ILogger<AiUsageLimiter> logger)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _settings = settings?.Value ?? new OpenAiSettings();
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AiUsageLimitDecision> CheckAsync(CancellationToken cancellationToken = default)
        {
            if (_settings.MaximumAiCallsPerMinute <= 0
                || _settings.MaximumAiCallsPerDay <= 0
                || _settings.MaximumAiInputTokensPerDay <= 0
                || _settings.MaximumAiOutputTokensPerDay <= 0)
            {
                return AiUsageLimitDecision.Reject("AI usage limits must be configured with positive values.");
            }

            var now = _clock.UtcNow;
            var minuteStart = now.AddMinutes(-1);
            var dayStart = now.Date;

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var callsLastMinute = await db.AiApiUsageRecords
                    .AsNoTracking()
                    .CountAsync(x => x.TimestampUtc >= minuteStart, cancellationToken)
                    .ConfigureAwait(false);
                if (callsLastMinute >= _settings.MaximumAiCallsPerMinute)
                {
                    return AiUsageLimitDecision.Reject($"Calls in the last minute are {callsLastMinute}, limit is {_settings.MaximumAiCallsPerMinute}.");
                }

                var daily = await db.AiApiUsageRecords
                    .AsNoTracking()
                    .Where(x => x.TimestampUtc >= dayStart && x.TimestampUtc <= now)
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        Calls = g.Count(),
                        InputTokens = g.Sum(x => x.InputTokens ?? 0),
                        OutputTokens = g.Sum(x => x.OutputTokens ?? 0)
                    })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (daily == null) return AiUsageLimitDecision.Allow();

                if (daily.Calls >= _settings.MaximumAiCallsPerDay)
                {
                    return AiUsageLimitDecision.Reject($"Daily AI calls are {daily.Calls}, limit is {_settings.MaximumAiCallsPerDay}.");
                }

                if (daily.InputTokens >= _settings.MaximumAiInputTokensPerDay)
                {
                    return AiUsageLimitDecision.Reject($"Daily AI input tokens are {daily.InputTokens}, limit is {_settings.MaximumAiInputTokensPerDay}.");
                }

                if (daily.OutputTokens >= _settings.MaximumAiOutputTokensPerDay)
                {
                    return AiUsageLimitDecision.Reject($"Daily AI output tokens are {daily.OutputTokens}, limit is {_settings.MaximumAiOutputTokensPerDay}.");
                }

                return AiUsageLimitDecision.Allow();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                _logger.LogError(ex, "Failed to evaluate AI usage limits. AI must fail closed.");
                return AiUsageLimitDecision.Reject("AI usage limits could not be evaluated.");
            }
        }
    }
}
