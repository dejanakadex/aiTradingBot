using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.ShadowProbe;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

var settings = ParseArguments(args);
if (settings is null) return 2;
if (settings.ContractsConfigPath is not null)
    return await ContractProbeRunner.RunAsync(settings.ContractsConfigPath, settings.OutputPath, settings.Port, settings.ClientId);

var started = DateTime.UtcNow;
var thresholds = new ShadowFeedThresholds();
var accumulators = settings.Symbols.ToDictionary(symbol => symbol,
    symbol => new ShadowFeedAccumulator($"US-STK-{symbol}-SMART", symbol), StringComparer.OrdinalIgnoreCase);
using var broker = new IbkrBrokerService(
    new IbkrOptions { Host = "127.0.0.1", Port = settings.Port, ClientId = settings.ClientId, ReadOnlyDiagnostics = true },
    NullLogger<IbkrBrokerService>.Instance,
    Options.Create(new MarketDataCollectionSettings()),
    Options.Create(new TradingSettings
    {
        Instruments = settings.Symbols.Select(symbol => new InstrumentSettings
        {
            InstrumentId = $"US-STK-{symbol}-SMART", Symbol = symbol, TradingEnabled = false
        }).ToArray()
    }));
var subscriptions = new List<(string Symbol, ITickMarketDataSubscription Subscription)>();
var readers = new List<Task>();
var errors = new ConcurrentQueue<string>();
var marketDataErrors = new Dictionary<string, int?>();
int? serverVersion = null;
try
{
    await broker.StartAsync();
    serverVersion = broker.ServerVersion;
    using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(settings.Seconds));
    foreach (var symbol in settings.Symbols)
    {
        try
        {
            var subscription = await broker.SubscribeTicksAsync($"US-STK-{symbol}-SMART", symbol, duration.Token);
            subscriptions.Add((symbol, subscription));
            readers.Add(ReadTicksAsync(symbol, subscription, accumulators[symbol], errors, duration.Token));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Enqueue($"{symbol}: tick subscription failed ({ex.GetType().Name}).");
        }
    }
    if (subscriptions.Count > 0)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(settings.Seconds), duration.Token); }
        catch (OperationCanceledException) when (duration.IsCancellationRequested) { }
    }
    duration.Cancel();
    await Task.WhenAll(readers);
}
catch (Exception ex)
{
    errors.Enqueue($"Read-only broker operation failed ({ex.GetType().Name}).");
}
finally
{
    foreach (var symbol in settings.Symbols) marketDataErrors[symbol] = broker.GetMarketDataErrorCode(symbol);
    foreach (var (_, subscription) in subscriptions) await subscription.DisposeAsync();
    await broker.StopAsync();
}

var ended = DateTime.UtcNow;
var results = settings.Symbols.Select(symbol => accumulators[symbol].Finish(
    subscriptions.FirstOrDefault(item => item.Symbol == symbol).Subscription?.DroppedEvents ?? 0,
    marketDataErrors.GetValueOrDefault(symbol), thresholds)).ToArray();
var reasons = errors.ToList();
ShadowDecisionReport? assessment = null;
if (settings.DatabasePath is null)
    reasons.Add("Operational SQLite database was not provided; signal timing, risk, rejection and model/baseline checks are unavailable.");
else
{
    try
    {
        assessment = await ShadowDecisionAssessor.AssessAsync(settings.DatabasePath, started, ended,
            new ShadowPerformanceSettings());
        if (!assessment.Passed) reasons.Add("Decision, risk or model/baseline thresholds did not pass.");
    }
    catch (Exception ex) when (ex is FileNotFoundException or SqliteException or InvalidOperationException)
    {
        reasons.Add($"Operational shadow assessment unavailable ({ex.GetType().Name}).");
    }
}
if ((ended - started).TotalSeconds < thresholds.MinimumDurationSeconds)
    reasons.Add($"Observation duration below {thresholds.MinimumDurationSeconds} seconds.");
if (results.Any(item => !item.Passed)) reasons.Add("One or more instruments did not meet feed thresholds.");
var report = new ShadowFeedReport(started, ended, (int)(ended - started).TotalSeconds,
    serverVersion, thresholds, results, reasons.Count == 0, reasons)
{ DecisionAssessment = assessment };
var output = Path.GetFullPath(settings.OutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Read-only shadow feed report saved: {output}");
Console.WriteLine($"Feed threshold result: {(report.Passed ? "PASS" : "INCOMPLETE")}; instruments={results.Length}; duration={report.DurationSeconds}s.");
foreach (var result in results)
    Console.WriteLine($"{result.Symbol}: trade={result.TradeTicks}, bid={result.BidTicks}, ask={result.AskTicks}, dropped={result.DroppedEvents}, p95LagMs={result.P95ReceiveLagMilliseconds?.ToString("F0") ?? "n/a"}, marketDataError={result.BrokerMarketDataErrorCode?.ToString() ?? "none"}.");
return report.Passed ? 0 : 1;

static ProbeSettings? ParseArguments(string[] arguments)
{
    var port = 7497;
    var clientId = 87;
    var seconds = 1800;
    var symbols = new[] { "SPY" };
    string? output = null;
    string? database = null;
    string? contractsConfig = null;
    for (var i = 0; i < arguments.Length; i++)
    {
        if (i + 1 >= arguments.Length)
        {
            Console.Error.WriteLine("Every option requires a value.");
            return null;
        }
        var value = arguments[++i];
        switch (arguments[i - 1])
        {
            case "--port" when int.TryParse(value, out var parsedPort): port = parsedPort; break;
            case "--client-id" when int.TryParse(value, out var parsedId): clientId = parsedId; break;
            case "--seconds" when int.TryParse(value, out var parsedSeconds): seconds = parsedSeconds; break;
            case "--symbols": symbols = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); break;
            case "--output": output = value; break;
            case "--database": database = value; break;
            case "--contracts-config": contractsConfig = value; break;
            default: Console.Error.WriteLine("Unknown or invalid option."); return null;
        }
    }
    if (port is < 1 or > 65535 || clientId < 0 || seconds is < 5 or > 3600
        || symbols.Length == 0 || symbols.Any(x => x.Length is < 1 or > 8 || x.Any(c => !char.IsAsciiLetterOrDigit(c)))
        || string.IsNullOrWhiteSpace(output)
        || (contractsConfig is not null && (port is not (7497 or 4002) || database is not null)))
    {
        Console.Error.WriteLine("Usage: --output ABSOLUTE_PATH [--database SQLITE_PATH] [--port 7497] [--client-id 87] [--seconds 1800] [--symbols SPY,QQQ]. Contract-only mode: --contracts-config JSON_PATH --output ABSOLUTE_PATH [--port 7497|4002] [--client-id 87].");
        return null;
    }
    return new ProbeSettings(port, clientId, seconds, symbols, output, database, contractsConfig);
}

static async Task ReadTicksAsync(string symbol, ITickMarketDataSubscription subscription,
    ShadowFeedAccumulator accumulator, ConcurrentQueue<string> errors, CancellationToken token)
{
    try
    {
        await foreach (var item in subscription.Reader.ReadAllAsync(token))
            accumulator.Add(item, subscription.Reader.CanCount ? subscription.Reader.Count : 0);
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    catch (IOException)
    {
        errors.Enqueue($"{symbol}: broker tick stream terminated (IOException).");
    }
}

internal sealed record ProbeSettings(int Port, int ClientId, int Seconds, string[] Symbols, string OutputPath, string? DatabasePath, string? ContractsConfigPath);
