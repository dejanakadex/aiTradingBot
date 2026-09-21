using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public sealed class UnavailableIbkrAdapter : IIbkrAdapter
    {
        private readonly ILogger<UnavailableIbkrAdapter> _logger;

        public UnavailableIbkrAdapter(ILogger<UnavailableIbkrAdapter> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsConnected => false;

#pragma warning disable CS0067
        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;
        public event Func<bool, Task>? ReadinessChanged;
        public event Func<MarketBar, Task>? MarketBarReceived;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("No concrete IBKR adapter is registered. IBKR connectivity is unavailable and trading must remain disabled.");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    public sealed class UnavailableIbkrConnectionService : IIbkrConnectionService
    {
        private readonly ILogger<UnavailableIbkrConnectionService> _logger;

        public UnavailableIbkrConnectionService(ILogger<UnavailableIbkrConnectionService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            Status = ConnectionStatus.Failed;
            _logger.LogWarning("No concrete IBKR connection service is registered. IBKR connection cannot be established.");
            if (ConnectionStatusChanged != null)
            {
                await ConnectionStatusChanged(Status).ConfigureAwait(false);
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Status = ConnectionStatus.Disconnected;
            if (ConnectionStatusChanged != null)
            {
                await ConnectionStatusChanged(Status).ConfigureAwait(false);
            }
        }
    }

    public sealed class UnavailableAccountService : IAccountService
    {
#pragma warning disable CS0067
        public event Func<AccountInfo, Task>? AccountUpdated;
#pragma warning restore CS0067

        public Task<AccountInfo> GetAccountInfoAsync(string accountId, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("IBKR account service is unavailable because no concrete IBKR integration is registered.");
        }
    }

    public sealed class UnavailablePositionService : IPositionService
    {
#pragma warning disable CS0067
        public event Func<PositionDto, Task>? PositionUpdated;
#pragma warning restore CS0067

        public Task<IEnumerable<PositionDto>> GetPositionsAsync(string accountId, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("IBKR position service is unavailable because no concrete IBKR integration is registered.");
        }
    }
}
