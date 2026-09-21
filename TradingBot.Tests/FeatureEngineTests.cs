using System;
using System.Collections.Generic;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class FeatureEngineTests
    {
        [Fact]
        public void Ema_Computation_Period3_KnownSequence()
        {
            // closes: 10,11,12,13 -> EMA period 3 -> alpha=0.5, initial SMA=(10+11+12)/3=11 -> next EMA=(13-11)*0.5+11=12
            var candles = new List<Candle>
            {
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-3), 0,0,0,10,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-2), 0,0,0,11,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1), 0,0,0,12,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 0,0,0,13,1),
            };

            var eng = new FeatureEngine(emaShort:3, emaLong:5, rsi:2, atr:2, volAvg:3);
            var f = eng.ComputeFeatures(candles);
            Assert.NotNull(f.EmaShort);
            Assert.Equal(12m, Math.Round(f.EmaShort.Value, 6));
        }

        [Fact]
        public void Rsi_Computation_Simple()
        {
            // close sequence: 1,2,3 -> period 2 -> gains 1,1 -> avgGain=1 avgLoss=0 -> RSI=100
            var candles = new List<Candle>
            {
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-2),0,0,0,1,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1),0,0,0,2,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow,0,0,0,3,1),
            };
            var eng = new FeatureEngine(emaShort:3, emaLong:5, rsi:2, atr:2, volAvg:3);
            var f = eng.ComputeFeatures(candles);
            Assert.Equal(100m, f.Rsi);
        }

        [Fact]
        public void Vwap_Computation_Known()
        {
            // two candles: typical prices 10 & 20, volumes 1 & 1 -> VWAP=(10+20)/2=15
            var candles = new List<Candle>
            {
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1),9,11,9,10,1),
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow,19,21,19,20,1),
            };

            var eng = new FeatureEngine();
            var f = eng.ComputeFeatures(candles);
            Assert.NotNull(f.Vwap);
            Assert.Equal(15m, Math.Round(f.Vwap.Value, 6));
        }

        [Fact]
        public void VolumeRatio_Computation()
        {
            // last volume 2, avg previous 2 over period 2 => ratio 1
            var candles = new List<Candle>
            {
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-2),0,0,0,10,2),
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1),0,0,0,11,2),
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow,0,0,0,12,2),
            };
            var eng = new FeatureEngine(volAvg:2);
            var f = eng.ComputeFeatures(candles);
            Assert.Equal(1m, f.VolumeRatio);
        }
    }
}
