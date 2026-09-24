using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    public sealed class PatternDetectorFactory : IPatternDetectorFactory
    {
        private readonly PatternDetectorOptions _options;
        private readonly ILoggerFactory _loggerFactory;

        public PatternDetectorFactory(IOptions<PatternDetectorOptions> options, ILoggerFactory loggerFactory)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            var canonicalOptions = JsonSerializer.Serialize(_options, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var optionsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalOptions))).ToLowerInvariant();
            DetectorVersion = $"{TradingBot.Domain.Models.PipelineContractVersions.Patterns}+config-{optionsHash}";
        }

        public string DetectorVersion { get; }

        public IPatternDetector Create() => new PatternDetector(
            _options,
            _loggerFactory.CreateLogger<PatternDetector>());
    }
}
