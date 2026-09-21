using System.Net;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class OpenAiErrorSanitizerTests
    {
        [Fact]
        public void BuildSafeMessage_BadRequestUnsupportedParameterKeepsUsefulDetail()
        {
            var body = """
            {
              "error": {
                "message": "Unsupported parameter: 'temperature' is not supported with this model."
              }
            }
            """;

            var message = OpenAiErrorSanitizer.BuildSafeMessage(HttpStatusCode.BadRequest, body);

            Assert.Contains("unsupported request parameter", message);
            Assert.Contains("temperature", message);
        }

        [Fact]
        public void BuildSafeMessage_BadRequestIncludesSanitizedOpenAiMessage()
        {
            var body = """
            {
              "error": {
                "message": "Invalid schema for response_format: required must include all properties."
              }
            }
            """;

            var message = OpenAiErrorSanitizer.BuildSafeMessage(HttpStatusCode.BadRequest, body);

            Assert.Contains("OpenAI rejected the request", message);
            Assert.Contains("Invalid schema", message);
        }
    }
}
