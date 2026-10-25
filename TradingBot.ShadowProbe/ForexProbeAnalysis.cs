using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.ShadowProbe;

/// <summary>Fresh research database, no model import, approvals, order lifecycle or execution service.</summary>
internal sealed class ForexProbeAnalysis
{
    private readonly ForexSignalHostedService _signals;
    private readonly IDbContextFactory<TradingBotDbContext> _db;
    private readonly IClock _clock;
    private readonly List<double> _milliseconds = [];
    public ForexProbeAnalysis(string path, IOptions<TradingSettings> trading, LatestMarketDataService latest,
        IDbContextFactory<TradingBotDbContext> db, IMarketDatasetStore dataset, IClock clock, IIbkrConnectionService connection)
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(path)).Build();
        // Probe collectors use these same default aggregate settings; custom policies must be measured in the full host.
        if (config.GetSection("ShortAggregates").Exists()) throw new ArgumentException("Probe uses default short aggregate settings.");
        var patterns = config.GetSection("ForexPatterns").Get<ForexPatternSettings>() ?? new();
        patterns.ReplaceConfiguredArrays(config.GetSection("ForexPatterns"));
        var signals = config.GetSection("ForexSignals").Get<ForexSignalSettings>() ?? new();
        if (!signals.Profiles.Any(x => x.Selection.Enabled)) throw new ArgumentException("An enabled FX signal profile is required.");
        var features = new FeatureEngine();
        var aggregate = Options.Create(new ShortAggregateSettings());
        var research = new CandidateResearchService(dataset, db, clock, Options.Create(new CandidateLabelingSettings()), NullLogger<CandidateResearchService>.Instance);
        var trigger = new ForexSignalTriggerService(Options.Create(signals), Options.Create(patterns), trading, aggregate, latest,
            new ForexFeatureService(latest, features, trading, aggregate), features, research, db, NullLoggerFactory.Instance);
        _signals = new(trigger, latest, Options.Create(signals), connection, clock, NullLogger<ForexSignalHostedService>.Instance);
        _db = db; _clock = clock;
    }
    public async Task RunAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await _signals.RunOnceAsync(_clock.UtcNow, ct);
        _milliseconds.Add(watch.Elapsed.TotalMilliseconds);
    }
    public async Task<object> ReportAsync()
    {
        await using var db = await _db.CreateDbContextAsync();
        var timings = _milliseconds.Order().ToArray();
        var decisions = await db.ResearchCandidateRecords.AsNoTracking().Where(x => x.ReplayRunId == null).ToListAsync();
        return new { Cycles = timings.Length, CycleP95Milliseconds = timings.Length == 0 ? (double?)null : timings[(int)Math.Ceiling(timings.Length * .95) - 1],
            PatternDecisions = decisions.Count, AcceptedSignals = decisions.Count(x => x.DecisionStage == ForexSignalTriggerService.AcceptedStage),
            Stages = decisions.GroupBy(x => x.DecisionStage).ToDictionary(x => x.Key, x => x.Count()),
            ModelShadowValidated = false, Orders = await db.OrderRecords.CountAsync(),
            Note = "Cycle includes idle cycles; not model or broker latency. Signal approval remains explicit; no approved model in this isolated database." };
    }
}
