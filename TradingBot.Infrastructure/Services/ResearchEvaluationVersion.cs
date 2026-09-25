using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Application.Configuration;

namespace TradingBot.Infrastructure.Services
{
    internal static class ResearchEvaluationVersion
    {
        public const string Contract = "evaluation-v1";

        public static string Create(ResearchEvaluationSettings settings)
        {
            var canonical = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            return $"{Contract}+config-{hash}";
        }
    }
}
