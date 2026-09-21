using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class PositionSizer : IPositionSizer
    {
        private readonly RiskSettings _settings;

        public PositionSizer(IOptions<RiskSettings> settings)
        {
            _settings = settings?.Value ?? new RiskSettings();
        }

        public PositionSizingResult CalculatePositionSize(
            decimal entryPrice,
            decimal stopPrice,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions)
        {
            var reasons = new List<string>();

            if (entryPrice <= 0m) reasons.Add("Entry price must be positive.");
            if (stopPrice <= 0m) reasons.Add("Stop price must be positive.");
            if (stopPrice >= entryPrice) reasons.Add("Stop price must be below entry price for a long trade.");
            if (accountInfo == null) reasons.Add("Account information is unavailable.");
            if (currentPositions == null) reasons.Add("Current broker positions are unavailable.");

            if (reasons.Count > 0 || accountInfo == null || currentPositions == null)
            {
                return new PositionSizingResult { RejectionReasons = reasons };
            }

            if (accountInfo.BuyingPower <= 0m) reasons.Add("Buying power is unavailable or exhausted.");
            if (accountInfo.NetLiquidation <= 0m) reasons.Add("Net liquidation value is unavailable.");

            var riskPerShare = entryPrice - stopPrice;
            if (riskPerShare <= 0m) reasons.Add("Risk per share must be positive.");

            if (reasons.Count > 0)
            {
                return new PositionSizingResult { RejectionReasons = reasons };
            }

            var currentPositionValue = currentPositions.Sum(p => Math.Abs(p.Quantity * p.AveragePrice));
            var leverageCap = accountInfo.NetLiquidation * Math.Max(0m, _settings.MaximumLeverage);
            var leverageRemaining = Math.Max(0m, leverageCap - currentPositionValue);

            var maxByRisk = _settings.MaximumRiskPerTrade / riskPerShare;
            var maxByPositionValue = _settings.MaximumPositionValue / entryPrice;
            var maxByBuyingPower = accountInfo.BuyingPower / entryPrice;
            var maxByLeverage = leverageRemaining / entryPrice;

            var quantity = new[] { maxByRisk, maxByPositionValue, maxByBuyingPower, maxByLeverage }
                .Where(q => q > 0m)
                .DefaultIfEmpty(0m)
                .Min();

            if (quantity <= 0m)
            {
                reasons.Add("No position size remains after risk, buying power and leverage limits.");
                return new PositionSizingResult { RejectionReasons = reasons };
            }

            return new PositionSizingResult
            {
                Quantity = quantity,
                PositionValue = quantity * entryPrice,
                RiskAmount = quantity * riskPerShare,
                RejectionReasons = Array.Empty<string>()
            };
        }
    }
}
