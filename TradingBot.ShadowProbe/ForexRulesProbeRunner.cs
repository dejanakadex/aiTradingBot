using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.ShadowProbe;

/// <summary>Read-only contract/market-rule capture. Size limits must be independently reviewed first.</summary>
public static class ForexRulesProbeRunner
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static (TradingSettings Trading, ForexExecutionSettings Execution, ConfiguredInstrument Instrument)
        ReadConfiguration(string json, DateTime nowUtc)
    {
        var trading = ContractProbeRunner.ReadConfiguration(json);
        if (trading.ForexPaperExecutionEnabled || nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Rules probe requires disabled FX execution and a UTC clock.");
        using var doc = JsonDocument.Parse(json);
        var execution = doc.RootElement.GetProperty("ForexExecution").Deserialize<ForexExecutionSettings>(Json)
            ?? throw new ArgumentException("Reviewed FX size limits are required.");
        execution.Validate();
        if (execution.Profiles.Length != 1) throw new ArgumentException("Select exactly one reviewed FX instrument.");
        var profile = execution.Profiles[0];
        if (profile.ReviewedAtUtc > nowUtc || nowUtc - profile.ReviewedAtUtc > TimeSpan.FromHours(24))
            throw new ArgumentException("Size review must be in the past and no older than 24 hours.");
        var instrument = trading.GetConfiguredInstruments().SingleOrDefault(x => x.InstrumentId == profile.InstrumentId);
        if (instrument?.InstrumentType != InstrumentType.Forex || instrument.Currency != "USD")
            throw new ArgumentException("A configured USD-quoted CASH instrument is required.");
        return (trading, execution, instrument);
    }

    public static async Task<int> RunAsync(string configPath, string outputPath, int port, int clientId)
    {
        // Validate everything before opening a broker connection. Never overwrite prior evidence.
        if (port is not (7497 or 4002) || clientId < 0 || !Path.IsPathFullyQualified(outputPath)
            || File.Exists(outputPath) || Directory.Exists(outputPath)) return 2;
        (TradingSettings Trading, ForexExecutionSettings Execution, ConfiguredInstrument Instrument) config;
        try { config = ReadConfiguration(await File.ReadAllTextAsync(configPath), DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"Invalid read-only FX rules configuration ({ex.GetType().Name})."); return 2;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var broker = new IbkrBrokerService(new IbkrOptions
        { Host = "127.0.0.1", Port = port, ClientId = clientId, ReadOnlyDiagnostics = true },
            NullLogger<IbkrBrokerService>.Instance, tradingSettings: Options.Create(config.Trading),
            forexExecution: Options.Create(config.Execution));
        ForexBrokerRulesSnapshot? snapshot = null;
        string? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await broker.StartAsync(timeout.Token);
            snapshot = await broker.GetForexOrderRulesAsync(config.Instrument, timeout.Token);
        }
        catch (Exception ex) { failure = $"Read-only rules capture failed ({ex.GetType().Name})."; }
        finally { await broker.StopAsync(); }
        var report = new
        {
            Passed = snapshot != null, CanSendOrders = false, config.Instrument.InstrumentId, Snapshot = snapshot, Failure = failure,
            Scope = "Contract and market-rule observations plus independently reviewed size limits; no order, account, margin, fill or odd-lot execution proof."
        };
        await using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(file, report, Json);
        Console.WriteLine($"Read-only FX rules evidence saved. Passed={report.Passed}; CanSendOrders=false.");
        return report.Passed ? 0 : 1;
    }
}
