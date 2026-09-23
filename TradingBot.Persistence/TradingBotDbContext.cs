using System;
using Microsoft.EntityFrameworkCore;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public class TradingBotDbContext : DbContext
    {
        public TradingBotDbContext(DbContextOptions<TradingBotDbContext> options) : base(options)
        {
        }

        public DbSet<Candle> Candles { get; set; }
        public DbSet<MarketSnapshot> MarketSnapshots { get; set; }
        public DbSet<PatternDetection> PatternDetections { get; set; }
        public DbSet<AiAnalysisRecord> AiAnalysisRecords { get; set; }
        public DbSet<AiApiUsageRecord> AiApiUsageRecords { get; set; }
        public DbSet<StrategyDecisionRecord> StrategyDecisionRecords { get; set; }
        public DbSet<RiskDecisionRecord> RiskDecisionRecords { get; set; }
        public DbSet<Trade> Trades { get; set; }
        public DbSet<OrderRecord> OrderRecords { get; set; }
        public DbSet<ExecutionRecord> ExecutionRecords { get; set; }
        public DbSet<BotSession> BotSessions { get; set; }
        public DbSet<PostTradeAnalysisRecord> PostTradeAnalysisRecords { get; set; }
        public DbSet<ExitManagementRecord> ExitManagementRecords { get; set; }
        public DbSet<ExitStopAuditRecord> ExitStopAuditRecords { get; set; }
        public DbSet<InstrumentRegistryRecord> InstrumentRegistryRecords { get; set; }
        public DbSet<InstrumentStatusTransitionRecord> InstrumentStatusTransitionRecords { get; set; }
        public DbSet<MarketDataStreamStateRecord> MarketDataStreamStateRecords { get; set; }
        public DbSet<MarketDataQualityIncidentRecord> MarketDataQualityIncidentRecords { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Candle>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.Symbol, x.Timeframe, x.TimestampUtc }).IsUnique();
                b.HasIndex(x => x.InstrumentId);
                b.HasIndex(x => x.ReceivedTimeUtc);
                b.HasIndex(x => x.QualityStatus);
            });

            modelBuilder.Entity<Trade>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.OpenedUtc);
                b.HasIndex(x => x.ClosedUtc);
                b.HasIndex(x => x.Symbol);
            });

            modelBuilder.Entity<PostTradeAnalysisRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.SourceTradeId).IsUnique(false);
                b.HasIndex(x => x.Symbol);
                b.HasIndex(x => x.Pattern);
                b.HasIndex(x => x.ClosedUtc);
                b.HasIndex(x => x.MarketRegime);
                b.HasIndex(x => x.AboveVwap);
                b.HasIndex(x => x.FifteenMinuteTrend);
                b.HasIndex(x => x.AiDecision);
            });

            modelBuilder.Entity<ExitManagementRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Symbol);
                b.HasIndex(x => x.EntryBrokerOrderId).IsUnique();
                b.HasIndex(x => x.ProtectiveStopBrokerOrderId);
                b.HasIndex(x => x.State);
                b.HasIndex(x => x.UpdatedUtc);
            });

            modelBuilder.Entity<ExitStopAuditRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.TradeId);
                b.HasIndex(x => x.Symbol);
                b.HasIndex(x => x.EntryBrokerOrderId);
                b.HasIndex(x => x.BrokerOrderId);
                b.HasIndex(x => x.TimestampUtc);
            });

            modelBuilder.Entity<OrderRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.BrokerOrderId).IsUnique(false);
                b.HasIndex(x => x.ClientOrderKey).IsUnique().HasFilter("ClientOrderKey <> ''");
            });

            modelBuilder.Entity<PatternDetection>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.PatternType);
                b.HasIndex(x => x.Symbol);
            });

            modelBuilder.Entity<AiAnalysisRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.TimestampUtc);
                b.HasIndex(x => x.AgentType);
                b.HasIndex(x => x.Model);
                b.HasIndex(x => x.PromptVersion);
            });

            modelBuilder.Entity<AiApiUsageRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.TimestampUtc);
                b.HasIndex(x => x.AgentType);
                b.HasIndex(x => x.RequestType);
                b.HasIndex(x => x.Symbol);
                b.HasIndex(x => x.Pattern);
                b.HasIndex(x => x.Success);
                b.HasIndex(x => x.Model);
                b.HasIndex(x => x.PromptVersion);
            });

            // Basic indexes for other entities
            modelBuilder.Entity<ExecutionRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.BrokerExecutionId).IsUnique();
            });

            modelBuilder.Entity<InstrumentRegistryRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.InstrumentId).IsUnique();
                b.HasIndex(x => x.Symbol);
                b.HasIndex(x => x.Status);
                b.HasIndex(x => x.ConfiguredEnabled);
                b.HasIndex(x => x.UpdatedAtUtc);
                b.Property(x => x.InstrumentId).UseCollation("NOCASE");
                b.Property(x => x.Version).IsConcurrencyToken();
            });

            modelBuilder.Entity<InstrumentStatusTransitionRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.InstrumentId);
                b.HasIndex(x => x.ToStatus);
                b.HasIndex(x => x.TimestampUtc);
            });

            modelBuilder.Entity<MarketDataStreamStateRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.StreamKey).IsUnique();
                b.HasIndex(x => x.InstrumentId);
                b.HasIndex(x => x.Status);
                b.HasIndex(x => x.UpdatedAtUtc);
                b.Property(x => x.StreamKey).UseCollation("NOCASE");
                b.Property(x => x.Version).IsConcurrencyToken();
            });

            modelBuilder.Entity<MarketDataQualityIncidentRecord>(b =>
            {
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.EventId);
                b.HasIndex(x => x.StreamKey);
                b.HasIndex(x => x.InstrumentId);
                b.HasIndex(x => x.Status);
                b.HasIndex(x => x.RecordedAtUtc);
            });
        }
    }
}
