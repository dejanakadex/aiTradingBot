using System;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class TradingPipelineStatusService : ITradingPipelineStatusService
    {
        private static readonly TimeSpan TerminalVisibleFor = TimeSpan.FromSeconds(8);
        private readonly IClock _clock;
        private readonly object _sync = new();
        private TradingPipelineActivitySnapshot _current;

        public TradingPipelineStatusService(IClock clock)
        {
            _clock = clock;
            _current = WaitingSnapshot(_clock.UtcNow, "Waiting for next candle.");
        }

        public TradingPipelineActivitySnapshot Current
        {
            get
            {
                lock (_sync)
                {
                    if (IsExpiredTerminalState(_current))
                    {
                        _current = WaitingSnapshot(_clock.UtcNow, "Waiting for next candle.");
                    }

                    return _current;
                }
            }
        }

        public void MarkWaiting(string status = "Waiting for next candle.")
        {
            lock (_sync)
            {
                _current = WaitingSnapshot(_clock.UtcNow, status);
            }
        }

        public void Mark(
            TradingPipelineStage stage,
            TradingPipelineActivityState state,
            string status,
            string? symbol = null,
            string? pattern = null)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                status = DefaultStatus(stage, state);
            }

            lock (_sync)
            {
                _current = new TradingPipelineActivitySnapshot
                {
                    Stage = stage,
                    State = state,
                    Title = TitleFor(stage),
                    Status = status,
                    Symbol = symbol ?? _current.Symbol,
                    Pattern = pattern ?? _current.Pattern,
                    UpdatedAtUtc = _clock.UtcNow
                };
            }
        }

        private bool IsExpiredTerminalState(TradingPipelineActivitySnapshot snapshot)
        {
            if (snapshot.State is not (TradingPipelineActivityState.Completed or TradingPipelineActivityState.Rejected or TradingPipelineActivityState.Error))
            {
                return false;
            }

            return _clock.UtcNow - snapshot.UpdatedAtUtc > TerminalVisibleFor;
        }

        private static TradingPipelineActivitySnapshot WaitingSnapshot(DateTime nowUtc, string status)
        {
            return new TradingPipelineActivitySnapshot
            {
                Stage = TradingPipelineStage.WaitingForCandle,
                State = TradingPipelineActivityState.Waiting,
                Title = TitleFor(TradingPipelineStage.WaitingForCandle),
                Status = status,
                UpdatedAtUtc = nowUtc
            };
        }

        private static string TitleFor(TradingPipelineStage stage)
        {
            return stage switch
            {
                TradingPipelineStage.WaitingForCandle => "Waiting",
                TradingPipelineStage.ReceivingCandle => "Candle",
                TradingPipelineStage.FetchingMarketContext => "Fetching",
                TradingPipelineStage.PatternAnalysis => "Pattern",
                TradingPipelineStage.AiAnalyzer => "Analyzer",
                TradingPipelineStage.AiCritic => "Critic",
                TradingPipelineStage.StrategyAndRisk => "Rules",
                TradingPipelineStage.OrderExecution => "Order",
                _ => "Pipeline"
            };
        }

        private static string DefaultStatus(TradingPipelineStage stage, TradingPipelineActivityState state)
        {
            if (state == TradingPipelineActivityState.Rejected) return "Rejected.";
            if (state == TradingPipelineActivityState.Completed) return "Completed.";
            if (state == TradingPipelineActivityState.Error) return "Error.";

            return stage switch
            {
                TradingPipelineStage.WaitingForCandle => "Waiting for next candle.",
                TradingPipelineStage.ReceivingCandle => "New candle received.",
                TradingPipelineStage.FetchingMarketContext => "Fetching recent market context.",
                TradingPipelineStage.PatternAnalysis => "Analyzing pattern quality.",
                TradingPipelineStage.AiAnalyzer => "Sending setup to AI analyzer.",
                TradingPipelineStage.AiCritic => "Sending BUY setup to AI critic.",
                TradingPipelineStage.StrategyAndRisk => "Checking strategy and risk rules.",
                TradingPipelineStage.OrderExecution => "Processing approved order.",
                _ => "Pipeline active."
            };
        }
    }
}
