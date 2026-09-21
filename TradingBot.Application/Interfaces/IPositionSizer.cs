using System.Collections.Generic;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IPositionSizer
    {
        PositionSizingResult CalculatePositionSize(
            decimal entryPrice,
            decimal stopPrice,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions);
    }
}
