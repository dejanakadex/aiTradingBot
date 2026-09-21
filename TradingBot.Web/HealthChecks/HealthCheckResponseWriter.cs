using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TradingBot.Web.HealthChecks
{
    public static class HealthCheckResponseWriter
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        public static Task WriteJsonAsync(HttpContext context, HealthReport report)
        {
            context.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                utc = DateTime.UtcNow,
                checks = report.Entries.ToDictionary(
                    entry => entry.Key,
                    entry => new
                    {
                        status = entry.Value.Status.ToString(),
                        description = entry.Value.Description,
                        durationMs = entry.Value.Duration.TotalMilliseconds,
                        error = entry.Value.Exception?.GetType().Name
                    })
            };

            return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
        }

        public static HealthCheckOptions AlwaysOkOptions()
        {
            return new HealthCheckOptions
            {
                ResponseWriter = WriteJsonAsync,
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [HealthStatus.Degraded] = StatusCodes.Status200OK,
                    [HealthStatus.Unhealthy] = StatusCodes.Status200OK
                }
            };
        }

        public static HealthCheckOptions DefaultOptions()
        {
            return new HealthCheckOptions
            {
                ResponseWriter = WriteJsonAsync
            };
        }
    }
}
