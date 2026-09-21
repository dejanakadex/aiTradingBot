using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TradingBot.Infrastructure.Services
{
    internal sealed class OpenAiRequestException : HttpRequestException
    {
        public OpenAiRequestException(HttpStatusCode statusCode, string safeMessage)
            : base($"OpenAI request failed with HTTP {(int)statusCode} {statusCode}: {safeMessage}")
        {
            StatusCodeValue = statusCode;
            SafeMessage = safeMessage;
        }

        public HttpStatusCode StatusCodeValue { get; }
        public string SafeMessage { get; }
    }

    internal static class OpenAiErrorSanitizer
    {
        public static string BuildSafeMessage(HttpStatusCode statusCode, string responseBody)
        {
            var raw = TryReadOpenAiMessage(responseBody);
            var normalized = raw.ToLowerInvariant();

            if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                || normalized.Contains("api key", StringComparison.Ordinal)
                || normalized.Contains("unauthorized", StringComparison.Ordinal))
            {
                return "OpenAI API key is invalid or unauthorized.";
            }

            if (normalized.Contains("model", StringComparison.Ordinal)
                && (normalized.Contains("does not exist", StringComparison.Ordinal)
                    || normalized.Contains("not found", StringComparison.Ordinal)
                    || normalized.Contains("unavailable", StringComparison.Ordinal)
                    || normalized.Contains("access", StringComparison.Ordinal)))
            {
                return "Configured model is unavailable or inaccessible.";
            }

            if (statusCode == HttpStatusCode.TooManyRequests
                || normalized.Contains("rate limit", StringComparison.Ordinal)
                || normalized.Contains("quota", StringComparison.Ordinal)
                || normalized.Contains("billing", StringComparison.Ordinal))
            {
                return "OpenAI quota, billing, or rate limit prevented the request.";
            }

            if (statusCode == HttpStatusCode.BadRequest
                && normalized.Contains("unsupported", StringComparison.Ordinal)
                && normalized.Contains("parameter", StringComparison.Ordinal))
            {
                return $"OpenAI rejected an unsupported request parameter. {BuildSafeDetail(raw)}";
            }

            if (statusCode == HttpStatusCode.BadRequest
                && !string.IsNullOrWhiteSpace(raw))
            {
                return $"OpenAI rejected the request. {BuildSafeDetail(raw)}";
            }

            return $"OpenAI request failed with HTTP {(int)statusCode} {statusCode}.";
        }

        public static string BuildFailureReason(Exception exception)
        {
            return exception is OpenAiRequestException openAi
                ? $"HTTP {(int)openAi.StatusCodeValue}: {openAi.SafeMessage}"
                : exception.GetType().Name;
        }

        private static string TryReadOpenAiMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody)) return string.Empty;

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString() ?? string.Empty;
                    }

                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return error.GetString() ?? string.Empty;
                    }
                }
            }
            catch (JsonException)
            {
                return string.Empty;
            }

            return string.Empty;
        }

        private static string BuildSafeDetail(string raw)
        {
            var detail = raw
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

            if (detail.Length > 300)
            {
                detail = detail[..300] + "...";
            }

            return string.IsNullOrWhiteSpace(detail) ? string.Empty : $"Detail: {detail}";
        }
    }
}
