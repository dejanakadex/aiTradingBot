using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    internal static class CandidateLabelVersion
    {
        public static string Create(CandidateLabelingSettings settings)
        {
            var canonical = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            return $"{PipelineContractVersions.Labels}+config-{hash}";
        }
    }
}
