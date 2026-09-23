using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class DashboardService : IDashboardService
    {
        private static readonly TimeSpan DashboardBrokerReadTimeout = TimeSpan.FromSeconds(2);

        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ITradingEngineStatusService _statusService;
        private readonly IOperatingModeService _operatingModeService;
        private readonly ITradingPipelineStatusService _pipelineStatusService;
        private readonly IServiceProvider _serviceProvider;
        private readonly IbkrSettings _ibkrSettings;
        private readonly RiskSettings _riskSettings;
        private readonly TradingSettings _tradingSettings;
        private readonly OpenAiSettings _openAiSettings;
        private readonly IClock _clock;
        private readonly ILogger<DashboardService> _logger;

        public DashboardService(
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ITradingEngineStatusService statusService,
            IOperatingModeService operatingModeService,
            ITradingPipelineStatusService pipelineStatusService,
            IServiceProvider serviceProvider,
            IOptions<IbkrSettings> ibkrSettings,
            IOptions<RiskSettings> riskSettings,
            IOptions<TradingSettings> tradingSettings,
            IOptions<OpenAiSettings> openAiSettings,
            IClock clock,
            ILogger<DashboardService> logger)
        {
            _dbFactory = dbFactory;
            _statusService = statusService;
            _operatingModeService = operatingModeService;
            _pipelineStatusService = pipelineStatusService;
            _serviceProvider = serviceProvider;
            _ibkrSettings = ibkrSettings.Value;
            _riskSettings = riskSettings.Value;
            _tradingSettings = tradingSettings.Value;
            _openAiSettings = openAiSettings.Value;
            _clock = clock;
            _logger = logger;
        }

        public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var today = _clock.UtcNow.Date;
            var status = _statusService.Current;
            var symbol = "SPY";

            var latestCandle = await db.Candles.AsNoTracking()
                .OrderByDescending(c => c.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var candlesToday = await db.Candles.AsNoTracking()
                .Where(c => c.TimestampUtc >= today)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);

            if (latestCandle != null) symbol = latestCandle.Symbol;

            var trades = await db.Trades.AsNoTracking()
                .Where(t => t.OpenedUtc >= today || (t.ClosedUtc.HasValue && t.ClosedUtc.Value >= today))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var closedTrades = trades.Where(t => t.ClosedUtc.HasValue && t.ExitPrice.HasValue).ToList();
            var grossPnl = closedTrades.Sum(t => (t.ExitPrice!.Value - t.EntryPrice) * t.Size);
            var wins = closedTrades.Count(t => (t.ExitPrice!.Value - t.EntryPrice) * t.Size > 0m);
            var losses = closedTrades.Count(t => (t.ExitPrice!.Value - t.EntryPrice) * t.Size < 0m);
            var fees = await EstimateTodayFeesAsync(db, today, cancellationToken).ConfigureAwait(false);

            var position = await GetCurrentPositionAsync(symbol, cancellationToken).ConfigureAwait(false);
            var account = await GetAccountAsync(cancellationToken).ConfigureAwait(false);
            var exposure = Math.Abs(position * (latestCandle?.Close ?? 0m));
            decimal? leverage = account?.NetLiquidation > 0m ? exposure / account.NetLiquidation : null;

            var latestAnalyzer = await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.AgentType == nameof(AiAgentType.Analyzer))
                .Where(a => a.TimestampUtc >= today)
                .OrderByDescending(a => a.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var latestCritic = await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.AgentType == nameof(AiAgentType.Critic))
                .Where(a => a.TimestampUtc >= today)
                .OrderByDescending(a => a.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var pipelineRows = await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.AgentType == nameof(AiAgentType.Pipeline))
                .Where(a => a.TimestampUtc >= today)
                .GroupBy(a => a.Context)
                .Select(g => new { RecordType = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var latestPipeline = await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.AgentType == nameof(AiAgentType.Pipeline))
                .Where(a => a.TimestampUtc >= today)
                .OrderByDescending(a => a.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var recentPatternGateRejections = (await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.AgentType == nameof(AiAgentType.Pipeline))
                .Where(a => a.Context == "PatternRejectedBeforeAi")
                .Where(a => a.TimestampUtc >= today)
                .OrderByDescending(a => a.TimestampUtc)
                .Take(5)
                .Select(a => new { a.TimestampUtc, a.ResultJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
                .Select(row => ToPatternGateRejection(row.TimestampUtc, row.ResultJson))
                .Where(row => row != null)
                .Select(row => row!)
                .ToArray();

            var latestStrategy = await db.StrategyDecisionRecords.AsNoTracking()
                .Where(s => s.TimestampUtc >= today)
                .OrderByDescending(s => s.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var latestRisk = await db.RiskDecisionRecords.AsNoTracking()
                .Where(r => r.TimestampUtc >= today)
                .OrderByDescending(r => r.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var latestOrder = await db.OrderRecords.AsNoTracking()
                .Where(o => o.CreatedUtc >= today)
                .OrderByDescending(o => o.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var analyzerDecisionCounts = await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.AgentType == nameof(AiAgentType.Analyzer))
                .Where(a => a.TimestampUtc >= today)
                .Select(a => a.ResultJson)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var aiUsageRows = await db.AiApiUsageRecords.AsNoTracking()
                .Where(a => a.TimestampUtc >= today)
                .Where(a => a.RequestType != "SmokeTest")
                .GroupBy(a => new { a.AgentType, a.RequestType })
                .Select(g => new
                {
                    g.Key.AgentType,
                    g.Key.RequestType,
                    Calls = g.Count(),
                    InputTokens = g.Sum(a => a.InputTokens ?? 0),
                    OutputTokens = g.Sum(a => a.OutputTokens ?? 0),
                    TotalTokens = g.Sum(a => a.TotalTokens ?? 0)
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var analyzerUsage = aiUsageRows
                .Where(a => IsAnalyzerUsage(a.AgentType, a.RequestType))
                .Aggregate(new AiUsageTotals(), (total, row) => total.Add(row.Calls, row.InputTokens, row.OutputTokens, row.TotalTokens));
            var criticUsage = aiUsageRows
                .Where(a => IsCriticUsage(a.AgentType, a.RequestType))
                .Aggregate(new AiUsageTotals(), (total, row) => total.Add(row.Calls, row.InputTokens, row.OutputTokens, row.TotalTokens));
            var totalUsage = aiUsageRows.Aggregate(new AiUsageTotals(), (total, row) => total.Add(row.Calls, row.InputTokens, row.OutputTokens, row.TotalTokens));
            var analyzerCost = EstimateCost(analyzerUsage.InputTokens, analyzerUsage.OutputTokens, _openAiSettings.Pricing.Analyzer);
            var criticCost = EstimateCost(criticUsage.InputTokens, criticUsage.OutputTokens, _openAiSettings.Pricing.Critic);
            var pipelineCount = (string recordType) => pipelineRows
                .Where(row => string.Equals(row.RecordType, recordType, StringComparison.OrdinalIgnoreCase))
                .Sum(row => row.Count);

            return new DashboardSnapshot
            {
                ApplicationState = "Running",
                OperatingMode = _operatingModeService.CurrentMode.ToString(),
                IbkrConnectionState = GetIbkrConnectionState(),
                IbkrEnvironmentVerification = status.BrokerEnvironmentVerification.ToString(),
                ConnectedAccountId = MaskAccount(status.ConnectedAccountId),
                ReconciliationState = status.ReconciliationCompleted ? "Reconciled" : status.State.ToString(),
                ReconciliationMessage = status.Mismatches.Count > 0
                    ? string.Join("; ", status.Mismatches)
                    : status.Message,
                KillSwitchActive = status.State == TradingBot.Domain.Enums.TradingEngineState.Faulted && status.Message.Contains("Kill switch", StringComparison.OrdinalIgnoreCase),
                LiveTradingExplicitlyEnabled = _tradingSettings.LiveTradingExplicitlyEnabled,
                PipelineActivity = _pipelineStatusService.Current,
                TradingEngineState = status.State,
                TradingEnabled = status.TradingEnabled && _tradingSettings.Enabled,
                CurrentSymbol = symbol,
                CurrentPrice = latestCandle?.Close,
                CurrentPosition = position,
                MarketDataStatus = GetMarketDataStatus(latestCandle),
                ConfiguredMarketData = $"{string.Join(",", GetConfiguredSymbols())} / {string.Join(",", GetConfiguredTimeframes())}",
                LatestCandleUtc = latestCandle?.TimestampUtc,
                CandlesToday = candlesToday,
                FifteenMinuteTrend = await GetTrendLabelAsync(db, symbol, Domain.Enums.Timeframe.FifteenMinutes, cancellationToken).ConfigureAwait(false),
                FiveMinuteMarketContext = await GetContextLabelAsync(db, symbol, Domain.Enums.Timeframe.FiveMinutes, cancellationToken).ConfigureAwait(false),
                OneMinuteDetectedPattern = await GetLatestPatternAsync(db, symbol, cancellationToken).ConfigureAwait(false),
                LatestAiAnalysis = ExtractJsonValue(latestAnalyzer?.ResultJson, "reason") ?? "None",
                AiConfidence = ExtractJsonDecimal(latestAnalyzer?.ResultJson, "confidence"),
                AnalyzerModel = _openAiSettings.Analyzer.Model,
                AnalyzerPrompt = _openAiSettings.Analyzer.PromptVersion,
                LatestAnalyzerDecision = ExtractJsonValue(latestAnalyzer?.ResultJson, "action") ?? "None",
                AnalyzerConfidence = ExtractJsonDecimal(latestAnalyzer?.ResultJson, "confidence"),
                LatestAnalyzerDecisionUtc = latestAnalyzer?.TimestampUtc,
                CriticModel = _openAiSettings.Critic.Model,
                CriticPrompt = _openAiSettings.Critic.PromptVersion,
                LatestCriticDecision = ExtractJsonValue(latestCritic?.ResultJson, "approved") ?? "None",
                LatestCriticDecisionUtc = latestCritic?.TimestampUtc,
                AiCallsToday = totalUsage.Calls,
                AnalyzerCallsToday = analyzerUsage.Calls,
                CriticCallsToday = criticUsage.Calls,
                PatternsRejectedBeforeAiToday = pipelineCount("PatternRejectedBeforeAi"),
                PatternsSentToAiToday = pipelineCount("PatternSentToAi") + pipelineCount("TradeSetupCandidateSentToAi"),
                RecentPatternGateRejections = recentPatternGateRejections,
                CriticSkippedToday = pipelineCount("CriticSkipped"),
                AnalyzerBuyToday = CountAnalyzerDecision(analyzerDecisionCounts, AiMarketActions.Buy),
                AnalyzerWaitToday = CountAnalyzerDecision(analyzerDecisionCounts, AiMarketActions.Wait),
                AnalyzerRejectToday = CountAnalyzerDecision(analyzerDecisionCounts, AiMarketActions.Reject),
                LatestPipelineDecision = latestPipeline?.Context ?? "None",
                LatestPipelineDecisionReason = ExtractJsonValue(latestPipeline?.ResultJson, "reason") ?? string.Empty,
                LatestPipelineDecisionUtc = latestPipeline?.TimestampUtc,
                LatestStrategyDecision = DescribeStrategyDecision(latestStrategy?.DecisionJson),
                LatestStrategyDecisionUtc = latestStrategy?.TimestampUtc,
                LatestRiskDecision = DescribeRiskDecision(latestRisk?.DecisionJson),
                LatestRiskDecisionUtc = latestRisk?.TimestampUtc,
                LatestOrderStatus = latestOrder == null ? "None" : $"{latestOrder.Status} {latestOrder.Symbol}",
                LatestOrderStatusUtc = latestOrder?.CreatedUtc,
                AnalyzerInputTokensToday = analyzerUsage.InputTokens,
                AnalyzerOutputTokensToday = analyzerUsage.OutputTokens,
                CriticInputTokensToday = criticUsage.InputTokens,
                CriticOutputTokensToday = criticUsage.OutputTokens,
                InputTokensToday = totalUsage.InputTokens,
                OutputTokensToday = totalUsage.OutputTokens,
                TotalTokensToday = totalUsage.TotalTokens,
                AnalyzerEstimatedCostToday = analyzerCost,
                CriticEstimatedCostToday = criticCost,
                AiEstimatedCostToday = analyzerCost + criticCost,
                TodaysTrades = closedTrades.Count,
                Wins = wins,
                Losses = losses,
                GrossProfitLoss = grossPnl,
                Fees = fees,
                NetProfitLoss = grossPnl - fees,
                CurrentExposure = exposure,
                CurrentLeverage = leverage,
                DailyLoss = grossPnl < 0m ? Math.Abs(grossPnl) : 0m,
                DailyLossLimit = _riskSettings.MaximumDailyLoss
            };
        }

        private async Task<decimal> GetCurrentPositionAsync(string symbol, CancellationToken cancellationToken)
        {
            if (_serviceProvider.GetService(typeof(IPositionService)) is not IPositionService positionService) return 0m;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DashboardBrokerReadTimeout);
                var positions = await positionService.GetPositionsAsync(ExpectedBrokerAccountId(), timeout.Token).ConfigureAwait(false);
                return positions.Where(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).Sum(p => p.Quantity);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Unable to read current broker position for {Symbol}; dashboard will show zero position.", symbol);
                return 0m;
            }
        }

        private string GetIbkrConnectionState()
        {
            if (_serviceProvider.GetService(typeof(IIbkrConnectionService)) is not IIbkrConnectionService connection)
            {
                return "Unavailable";
            }

            if (connection is UnavailableIbkrConnectionService)
            {
                return "Unavailable - IBKR adapter not registered";
            }

            var status = _statusService.Current;
            if (connection.Status == Application.DTOs.ConnectionStatus.Connected)
            {
                return "Connected";
            }

            var endpoint = $"{_ibkrSettings.Host}:{_ibkrSettings.GetPort(_tradingSettings.OperatingMode)}";
            var message = status.Mismatches.Count > 0
                ? status.Mismatches[0]
                : status.Message;

            return string.IsNullOrWhiteSpace(message)
                ? $"{connection.Status} - configured endpoint {endpoint}"
                : $"{connection.Status} - {message} Endpoint={endpoint}";
        }

        private async Task<Application.DTOs.AccountInfo?> GetAccountAsync(CancellationToken cancellationToken)
        {
            if (_serviceProvider.GetService(typeof(IAccountService)) is not IAccountService accountService) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DashboardBrokerReadTimeout);
                return await accountService.GetAccountInfoAsync(ExpectedBrokerAccountId(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Unable to read broker account information; dashboard leverage will be unavailable.");
                return null;
            }
        }

        private string ExpectedBrokerAccountId()
        {
            if (_tradingSettings.OperatingMode == TradingOperatingMode.PaperTrading)
            {
                return _ibkrSettings.PaperAccountId ?? string.Empty;
            }

            if (_tradingSettings.OperatingMode == TradingOperatingMode.AnalysisOnly
                && !string.IsNullOrWhiteSpace(_ibkrSettings.PaperAccountId))
            {
                return _ibkrSettings.PaperAccountId;
            }

            return _ibkrSettings.AccountId ?? string.Empty;
        }

        private string GetMarketDataStatus(TradingBot.Persistence.Candle? latestCandle)
        {
            if (latestCandle == null) return "No candles yet";

            var age = _clock.UtcNow - ToUtc(latestCandle.TimestampUtc);
            if (!IsWithinConfiguredMarketHours(_clock.UtcNow))
            {
                return $"Market closed (last candle {FormatAge(age)} old)";
            }

            return age > TimeSpan.FromSeconds(Math.Max(1, _tradingSettings.MaximumCandleAgeSeconds))
                ? $"Stale ({FormatAge(age)} old)"
                : $"Receiving ({FormatAge(age)} old)";
        }

        private IReadOnlyList<string> GetConfiguredSymbols()
        {
            var symbols = _tradingSettings.GetEnabledInstruments()
                .Select(instrument => instrument.Symbol)
                .ToArray();

            return symbols.Length == 0 ? new[] { "SPY" } : symbols;
        }

        private IReadOnlyList<string> GetConfiguredTimeframes()
        {
            var timeframes = _tradingSettings.GetEnabledInstruments()
                .SelectMany(instrument => instrument.MarketDataTimeframes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return timeframes.Length == 0 ? new[] { "1m", "5m", "15m" } : timeframes;
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero) return "0s";
            if (age.TotalSeconds < 60) return $"{Math.Floor(age.TotalSeconds)}s";
            if (age.TotalMinutes < 60) return $"{Math.Floor(age.TotalMinutes)}m";
            return $"{Math.Floor(age.TotalHours)}h";
        }

        private bool IsWithinConfiguredMarketHours(DateTime timestampUtc)
        {
            var eastern = ResolveEasternTimeZone();
            var local = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(timestampUtc), eastern);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

            var start = new TimeSpan(
                Math.Clamp(_tradingSettings.TradingStartHourNewYork, 0, 23),
                Math.Clamp(_tradingSettings.TradingStartMinuteNewYork, 0, 59),
                0);
            var end = new TimeSpan(
                Math.Clamp(_tradingSettings.TradingEndHourNewYork, 0, 23),
                Math.Clamp(_tradingSettings.TradingEndMinuteNewYork, 0, 59),
                0);
            var time = local.TimeOfDay;

            if (start == end) return true;
            if (start < end) return time >= start && time < end;
            return time >= start || time < end;
        }

        private static TimeZoneInfo ResolveEasternTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
        }

        private static DateTime ToUtc(DateTime timestamp)
        {
            return timestamp.Kind switch
            {
                DateTimeKind.Utc => timestamp,
                DateTimeKind.Local => timestamp.ToUniversalTime(),
                _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            };
        }

        private static async Task<decimal> EstimateTodayFeesAsync(TradingBot.Persistence.TradingBotDbContext db, DateTime today, CancellationToken cancellationToken)
        {
            var executions = await db.ExecutionRecords.AsNoTracking()
                .Where(e => e.TimestampUtc >= today)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return executions.Sum(e => ExtractJsonDecimal(e.RawJson, "commission") ?? 0m);
        }

        private static async Task<string> GetTrendLabelAsync(TradingBot.Persistence.TradingBotDbContext db, string symbol, Domain.Enums.Timeframe timeframe, CancellationToken cancellationToken)
        {
            var candles = await db.Candles.AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take(2)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (candles.Count < 2) return "Unknown";
            return candles[0].Close > candles[1].Close ? "Up" : candles[0].Close < candles[1].Close ? "Down" : "Flat";
        }

        private static async Task<string> GetContextLabelAsync(TradingBot.Persistence.TradingBotDbContext db, string symbol, Domain.Enums.Timeframe timeframe, CancellationToken cancellationToken)
        {
            var candles = await db.Candles.AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take(2)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (candles.Count < 2) return "Unknown";
            var change = candles[0].Close - candles[1].Close;
            return change > 0m ? "Constructive" : change < 0m ? "Pullback" : "Balanced";
        }

        private static async Task<string> GetLatestPatternAsync(TradingBot.Persistence.TradingBotDbContext db, string symbol, CancellationToken cancellationToken)
        {
            var pattern = await db.PatternDetections.AsNoTracking()
                .Where(p => p.Symbol == symbol)
                .OrderByDescending(p => p.DetectedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return pattern?.PatternType.ToString() ?? "None";
        }

        private static string DescribeStrategyDecision(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "None";

            var recordType = ExtractJsonValue(json, "recordType");
            if (string.Equals(recordType, "HypotheticalTrade", StringComparison.OrdinalIgnoreCase))
            {
                return "Hypothetical trade stored";
            }

            var approved = ExtractJsonValue(json, "approved");
            if (string.Equals(approved, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return "Approved";
            }

            if (string.Equals(approved, bool.FalseString, StringComparison.OrdinalIgnoreCase))
            {
                return "Rejected";
            }

            return recordType ?? "Recorded";
        }

        private static string DescribeRiskDecision(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "None";

            var decision = ExtractJsonValue(json, "decision")
                ?? ExtractJsonValue(json, "decisionType");
            if (!string.IsNullOrWhiteSpace(decision))
            {
                return decision;
            }

            var approved = ExtractJsonValue(json, "approved");
            if (string.Equals(approved, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return "Approved";
            }

            if (string.Equals(approved, bool.FalseString, StringComparison.OrdinalIgnoreCase))
            {
                return "Rejected";
            }

            return "Recorded";
        }

        private static string? ExtractJsonValue(string? json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!TryFindProperty(doc.RootElement, propertyName, out var value)) return null;
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
            catch (JsonException) { return null; }
        }

        private static decimal? ExtractJsonDecimal(string? json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!TryFindProperty(doc.RootElement, propertyName, out var value)) return null;
                return value.TryGetDecimal(out var number) ? number : null;
            }
            catch (JsonException) { return null; }
        }

        private static DateTime? ExtractJsonDateTime(string? json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!TryFindProperty(doc.RootElement, propertyName, out var value)) return null;

                if (value.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(value.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                {
                    return ToUtc(parsed);
                }

                return null;
            }
            catch (JsonException) { return null; }
        }

        private static PatternGateRejectionSnapshot? ToPatternGateRejection(DateTime timestampUtc, string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            return new PatternGateRejectionSnapshot
            {
                TimestampUtc = ToUtc(timestampUtc),
                Symbol = ExtractJsonValue(json, "symbol") ?? string.Empty,
                Pattern = ExtractJsonValue(json, "pattern") ?? string.Empty,
                Timeframe = ExtractJsonValue(json, "patternTimeframe") ?? string.Empty,
                PatternDetectedAtUtc = ExtractJsonDateTime(json, "patternDetectedAtUtc"),
                Quality = ExtractJsonDecimal(json, "deterministicPatternQuality")
                    ?? ExtractJsonDecimal(json, "tradeSetupQuality"),
                Reason = ExtractJsonValue(json, "reason") ?? string.Empty
            };
        }

        private static bool TryFindProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(propertyName, out value)) return true;
                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindProperty(property.Value, propertyName, out value)) return true;
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindProperty(item, propertyName, out value)) return true;
                }
            }

            value = default;
            return false;
        }

        private static bool IsAnalyzerUsage(string agentType, string requestType)
        {
            return string.Equals(agentType, nameof(AiAgentType.Analyzer), StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestType, "MarketAnalyzer", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCriticUsage(string agentType, string requestType)
        {
            return string.Equals(agentType, nameof(AiAgentType.Critic), StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestType, "TradeCritic", StringComparison.OrdinalIgnoreCase);
        }

        private static int CountAnalyzerDecision(IReadOnlyList<string> resultJsonRows, string action)
        {
            var needle = $"\"action\":\"{action}\"";
            return resultJsonRows.Count(json => !string.IsNullOrWhiteSpace(json)
                && json.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        private static decimal EstimateCost(int inputTokens, int outputTokens, OpenAiAgentPricingSettings pricing)
        {
            return (inputTokens / 1_000_000m * pricing.InputPricePerMillionTokens)
                + (outputTokens / 1_000_000m * pricing.OutputPricePerMillionTokens);
        }

        private static string MaskAccount(string? accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return string.Empty;
            return accountId.Length <= 4 ? "****" : $"{accountId[..2]}***{accountId[^2..]}";
        }

        private sealed class AiUsageTotals
        {
            public int Calls { get; private set; }
            public int InputTokens { get; private set; }
            public int OutputTokens { get; private set; }
            public int TotalTokens { get; private set; }

            public AiUsageTotals Add(int calls, int inputTokens, int outputTokens, int totalTokens)
            {
                Calls += calls;
                InputTokens += inputTokens;
                OutputTokens += outputTokens;
                TotalTokens += totalTokens;
                return this;
            }
        }
    }
}
