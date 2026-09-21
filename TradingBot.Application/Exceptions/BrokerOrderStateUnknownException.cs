using System;

namespace TradingBot.Application.Exceptions
{
    public sealed class BrokerOrderStateUnknownException : Exception
    {
        public BrokerOrderStateUnknownException(string message) : base(message)
        {
        }

        public BrokerOrderStateUnknownException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
