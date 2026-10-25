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

/// <summary>Contract-only diagnostic. Does not start the host, database, collectors or order workflow.</summary>
public static class ContractProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static TradingSettings ReadConfiguration(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("TradingSettings", out var section))
            throw new ArgumentException("Contracts config must contain TradingSettings.");
        var settings = section.Deserialize<TradingSettings>(JsonOptions)
            ?? throw new ArgumentException("TradingSettings is required.");
        if (settings.Enabled || settings.LiveTradingExplicitlyEnabled || settings.OperatingMode != TradingOperatingMode.AnalysisOnly)
            throw new ArgumentException("Contracts config must use disabled trading and AnalysisOnly mode.");
        if (settings.GetValidationErrors().Count != 0)
            throw new ArgumentException("Contracts config has invalid instrument settings.");
        var instruments = settings.GetConfiguredInstruments();
        if (instruments.Count == 0 || instruments.Any(item => item.TradingEnabled))
            throw new ArgumentException("Explicit instruments with TradingEnabled=false are required.");
        foreach (var instrument in instruments) _ = IbkrContractFactory.Create(instrument);
        return settings;
    }

    public static async Task<int> RunAsync(string configPath, string outputPath, int port, int clientId)
    {
        if (port is not (7497 or 4002) || clientId < 0) return 2;
        TradingSettings trading;
        try { trading = ReadConfiguration(await File.ReadAllTextAsync(configPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Invalid contract-only configuration ({ex.GetType().Name}). No broker connection opened.");
            return 2;
        }

        var started = DateTime.UtcNow;
        var results = new List<ContractProbeInstrumentReport>();
        var errors = new List<string>();
        using var broker = new IbkrBrokerService(new IbkrOptions
        {
            Host = "127.0.0.1", Port = port, ClientId = clientId, ReadOnlyDiagnostics = true
        }, NullLogger<IbkrBrokerService>.Instance, tradingSettings: Options.Create(trading));
        int? serverVersion = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await broker.StartAsync(timeout.Token);
            serverVersion = broker.ServerVersion;
            foreach (var instrument in trading.GetConfiguredInstruments())
            {
                var probe = await broker.ProbeContractAsync(instrument, timeout.Token);
                results.Add(new(instrument.InstrumentId, instrument.Symbol, IbkrContractFactory.Describe(instrument), probe));
            }
        }
        catch (Exception ex)
        {
            // Never export raw broker messages, account identifiers or local configuration contents.
            errors.Add(serverVersion is null
                ? $"Paper API connection was not established at 127.0.0.1:{port} ({ex.GetType().Name}); check TWS/Gateway login, socket API port and client ID."
                : $"Contract-only broker operation failed ({ex.GetType().Name}).");
        }
        finally { await broker.StopAsync(); }

        var passed = errors.Count == 0 && results.Count == trading.GetConfiguredInstruments().Count
            && results.All(item => item.Result.Status == "Confirmed");
        var report = new ContractProbeReport(started, DateTime.UtcNow, broker.ApiVersion, serverVersion, port,
            results, passed, errors);
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"Read-only contract report saved: {output}");
        foreach (var item in results)
            Console.WriteLine($"{item.Symbol}: {item.Result.Status}; contractId={item.Result.ContractId}; error={item.Result.ErrorCode}.");
        Console.WriteLine("Contract identity only; no market data, order, fill or trading-readiness confirmation.");
        return passed ? 0 : 1;
    }
}

public sealed record ContractProbeInstrumentReport(string InstrumentId, string Symbol,
    IbkrContractIdentity RequestedContract, IbkrContractProbe Result);
public sealed record ContractProbeReport(DateTime StartedUtc, DateTime EndedUtc, string ApiVersion,
    int? ServerVersion, int Port, IReadOnlyList<ContractProbeInstrumentReport> Instruments,
    bool Passed, IReadOnlyList<string> Reasons);
