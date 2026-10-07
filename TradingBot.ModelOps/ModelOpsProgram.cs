using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

internal static class ModelOpsProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                PrintUsage();
                return args.Length == 0 ? 2 : 0;
            }
            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1).ToArray());
            var suppliedDbPath = Required(options, "db");
            if (!Path.IsPathFullyQualified(suppliedDbPath)) throw new ArgumentException("--db must be an absolute path.");
            var dbPath = Path.GetFullPath(suppliedDbPath);
            if (!File.Exists(dbPath)) throw new ArgumentException($"Database does not exist: {dbPath}");
            var configPath = options.TryGetValue("config", out var configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine("TradingBot.Web", "appsettings.json"));
            if (!File.Exists(configPath)) throw new ArgumentException($"Configuration does not exist: {configPath}");
            var configuration = new ConfigurationBuilder().AddJsonFile(configPath).AddEnvironmentVariables().Build();
            var evaluationSettings = configuration.GetSection("ResearchEvaluation").Get<ResearchEvaluationSettings>() ?? new();
            var modelSettings = configuration.GetSection("NumericalModel").Get<NumericalModelSettings>() ?? new();
            var labelSettings = configuration.GetSection("CandidateLabeling").Get<CandidateLabelingSettings>() ?? new();
            var featureSettings = configuration.GetSection("CanonicalFeatures").Get<CanonicalFeatureSettings>() ?? new();
            var errors = evaluationSettings.GetValidationErrors().Concat(modelSettings.GetValidationErrors())
                .Concat(labelSettings.GetValidationErrors()).Concat(featureSettings.GetValidationErrors()).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(string.Join(" ", errors));

            var write = command is "migrate" or "evaluate" or "train" or "approve" or "reject" or "rollback";
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                DefaultTimeout = 10
            }.ToString();
            var services = new ServiceCollection();
            services.AddDbContextFactory<TradingBotDbContext>(builder => builder.UseSqlite(connection));
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<IOptions<ResearchEvaluationSettings>>(Options.Create(evaluationSettings));
            services.AddSingleton<IOptions<NumericalModelSettings>>(Options.Create(modelSettings));
            services.AddSingleton<IOptions<CandidateLabelingSettings>>(Options.Create(labelSettings));
            services.AddSingleton<IOptions<CanonicalFeatureSettings>>(Options.Create(featureSettings));
            services.AddSingleton<IResearchEvaluationService, ResearchEvaluationService>();
            services.AddSingleton<INumericalModelService, NumericalModelService>();
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                if (command == "migrate")
                {
                    await db.Database.MigrateAsync();
                    Print(new { migrated = dbPath, knownMigrations = db.Database.GetMigrations().Count(),
                        pending = (await db.Database.GetPendingMigrationsAsync()).ToArray() });
                    return 0;
                }
                var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
                if (pending.Length != 0) throw new InvalidOperationException("Database has pending migrations. Run the explicit 'migrate' command first.");
            }

            var evaluations = provider.GetRequiredService<IResearchEvaluationService>();
            var models = provider.GetRequiredService<INumericalModelService>();
            switch (command)
            {
                case "evaluate":
                    Print(await evaluations.RunAsync(new ResearchEvaluationRequest(
                        int.Parse(Required(options, "horizon-seconds"), CultureInfo.InvariantCulture),
                        ParseUtc(Required(options, "from")), ParseUtc(Required(options, "to")),
                        options.TryGetValue("replay-run-id", out var replayId) ? Guid.Parse(replayId) : null,
                        Optional(options, "instrument-id"), Optional(options, "strategy-id"))));
                    break;
                case "train":
                    Print(await models.TrainAsync(new NumericalModelTrainingRequest(Guid.Parse(Required(options, "evaluation-id")))));
                    break;
                case "list":
                    Print(await models.GetAllAsync(options.TryGetValue("count", out var count)
                        ? int.Parse(count, CultureInfo.InvariantCulture) : 50));
                    break;
                case "show":
                    Print(await models.GetAsync(Guid.Parse(Required(options, "model-id")))
                        ?? throw new KeyNotFoundException("Model was not found."));
                    break;
                case "compare":
                    var left = await models.GetAsync(Guid.Parse(Required(options, "left")))
                        ?? throw new KeyNotFoundException("Left model was not found.");
                    var right = await models.GetAsync(Guid.Parse(Required(options, "right")))
                        ?? throw new KeyNotFoundException("Right model was not found.");
                    if (left.InstrumentId != right.InstrumentId || left.StrategyId != right.StrategyId
                        || left.HorizonSeconds != right.HorizonSeconds || left.FeatureVersion != right.FeatureVersion
                        || left.PatternVersion != right.PatternVersion || left.LabelVersion != right.LabelVersion)
                        throw new InvalidOperationException("Comparison requires the same instrument, strategy, horizon and pipeline versions.");
                    Print(new { left, right, holdoutExpectancyDeltaBps = right.CandidateHoldout.ExpectancyBps - left.CandidateHoldout.ExpectancyBps,
                        walkForwardExpectancyDeltaBps = right.CandidateWalkForward.ExpectancyBps - left.CandidateWalkForward.ExpectancyBps });
                    break;
                case "approve":
                case "reject":
                    Print(await models.DecideAsync(Guid.Parse(Required(options, "model-id")),
                        new NumericalModelDecisionRequest(command == "approve", Required(options, "reviewer"),
                            Required(options, "reason"), Required(options, "sha256"))));
                    break;
                case "rollback":
                    Print(await models.RollbackAsync(Guid.Parse(Required(options, "model-id")),
                        new NumericalModelRollbackRequest(Required(options, "reviewer"), Required(options, "reason"),
                            Required(options, "sha256"))));
                    break;
                case "history":
                    Print(await models.GetDecisionHistoryAsync(Guid.Parse(Required(options, "model-id"))));
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{command}'. Run 'help' for usage.");
            }
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException
            or KeyNotFoundException or SqliteException)
        {
            Console.Error.WriteLine($"ModelOps: {error.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal)
                || !result.TryAdd(args[index][2..], args[index + 1]))
                throw new ArgumentException("Options must be unique '--name value' pairs.");
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required option --{name} is missing.");

    private static string? Optional(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : null;

    private static DateTime ParseUtc(string value)
    {
        if (!value.EndsWith('Z') && !value.EndsWith("+00:00", StringComparison.Ordinal))
            throw new ArgumentException("Evaluation timestamps must include a UTC offset (Z or +00:00).");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime;
    }

    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void PrintUsage() => Console.WriteLine("""
        Local model operations. Run from the repository root; no Web host, broker or trading worker is started.
        Every command requires --db ABSOLUTE_SQLITE_PATH; optional --config APPSETTINGS_JSON overrides TradingBot.Web/appsettings.json.

        migrate
        evaluate --instrument-id ID --strategy-id ID --horizon-seconds N --from UTC --to UTC [--replay-run-id GUID]
        train --evaluation-id GUID
        list [--count N]
        show --model-id GUID
        compare --left GUID --right GUID
        approve|reject --model-id GUID --reviewer NAME --reason TEXT --sha256 REVIEWED_OUTPUT_HASH
        rollback --model-id GUID --reviewer NAME --reason TEXT --sha256 TARGET_OUTPUT_HASH
        history --model-id GUID
        """);
}
