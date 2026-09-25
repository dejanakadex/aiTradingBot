using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    internal static class PatternDetectorVersion
    {
        public static string Create(PatternDetectorOptions options)
        {
            var canonicalOptions = JsonSerializer.Serialize(options, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var optionsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalOptions))).ToLowerInvariant();
            return $"{PipelineContractVersions.Patterns}+config-{optionsHash}";
        }
    }
}
