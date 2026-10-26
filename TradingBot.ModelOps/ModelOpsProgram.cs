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
using TradingBot.Infrastructure.Options;
using TradingBot.Persistence;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingBot.Tests")]

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
            if (command == "fx-collection-audit") return await AuditForexCollectionAsync(options);
            var suppliedDbPath = Required(options, "db");
            if (!Path.IsPathFullyQualified(suppliedDbPath)) throw new ArgumentException("--db must be an absolute path.");
            var dbPath = Path.GetFullPath(suppliedDbPath);
            if (command == "fx-paper-preflight")
            {
                var file = Path.GetFullPath(Required(options, "config"));
                // Explicit file only: never silently merge secrets or unrelated machine overrides into this offline report.
                var config = new ConfigurationBuilder().AddJsonFile(file).Build();
                var report = await ForexPaperPreflight.CheckAsync(config, dbPath, Required(options, "instrument-id"), Required(options, "strategy-id"), DateTime.UtcNow);
                Print(report);
                if (options.TryGetValue("output", out var output))
                {
                    if (!Path.IsPathFullyQualified(output) || File.Exists(output)) throw new ArgumentException("--output must be a new absolute report path.");
                    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions));
                }
                return report.OfflinePrerequisitesPassed ? 0 : 3;
            }
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
            // The binder appends configured arrays to nonempty defaults. Explicit lists must replace them.
            evaluationSettings.ConfidenceThresholds = configuration.GetSection("ResearchEvaluation:ConfidenceThresholds").Get<decimal[]>() ?? evaluationSettings.ConfidenceThresholds;
            evaluationSettings.CostStressMultipliers = configuration.GetSection("ResearchEvaluation:CostStressMultipliers").Get<decimal[]>() ?? evaluationSettings.CostStressMultipliers;
            modelSettings.ProbabilityThresholds = configuration.GetSection("NumericalModel:ProbabilityThresholds").Get<decimal[]>() ?? modelSettings.ProbabilityThresholds;
            labelSettings.HorizonsSeconds = configuration.GetSection("CandidateLabeling:HorizonsSeconds").Get<int[]>() ?? labelSettings.HorizonsSeconds;
            var aggregates = configuration.GetSection("ShortAggregates").Get<ShortAggregateSettings>() ?? new();
            aggregates.IntervalsSeconds = configuration.GetSection("ShortAggregates:IntervalsSeconds").Get<int[]>() ?? aggregates.IntervalsSeconds;
            var forexPatterns = configuration.GetSection("ForexPatterns").Get<ForexPatternSettings>() ?? new();
            forexPatterns.ReplaceConfiguredArrays(configuration.GetSection("ForexPatterns"));
            var errors = evaluationSettings.GetValidationErrors().Concat(modelSettings.GetValidationErrors())
                .Concat(labelSettings.GetValidationErrors()).Concat(featureSettings.GetValidationErrors()).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException(string.Join(" ", errors));

            var write = command is "migrate" or "evaluate" or "train" or "approve" or "reject" or "rollback" or "fx-replay";
            var connection = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
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
            services.AddLogging();
            services.AddSingleton<IOptions<TradingSettings>>(Options.Create(configuration.GetSection("TradingSettings").Get<TradingSettings>() ?? new()));
            services.AddSingleton<IOptions<RiskSettings>>(Options.Create(configuration.GetSection("RiskSettings").Get<RiskSettings>() ?? new()));
            services.AddSingleton<IOptions<ForexResearchSettings>>(Options.Create(configuration.GetSection("ForexResearch").Get<ForexResearchSettings>(o => o.ErrorOnUnknownConfiguration = true) ?? new()));
            services.AddSingleton<IOptions<ForexRiskSettings>>(Options.Create(configuration.GetSection("ForexRisk").Get<ForexRiskSettings>() ?? new()));
            services.AddSingleton<IOptions<ForexSignalSettings>>(Options.Create(configuration.GetSection("ForexSignals").Get<ForexSignalSettings>() ?? new()));
            services.AddSingleton<IOptions<ForexPatternSettings>>(Options.Create(forexPatterns));
            services.AddSingleton<IOptions<ShortAggregateSettings>>(Options.Create(aggregates));
            services.AddSingleton<ForexResearchContract>();
            if (command == "fx-replay")
            {
                var datasetPath = Required(options, "dataset");
                if (!Path.IsPathFullyQualified(datasetPath) || !Directory.Exists(datasetPath)) throw new ArgumentException("--dataset must name an existing absolute Parquet dataset directory.");
                services.AddSingleton<IOptions<DatasetStorageSettings>>(Options.Create(new DatasetStorageSettings { RootPath = datasetPath }));
                services.AddSingleton<IOptions<ReplaySettings>>(Options.Create(new ReplaySettings()));
                services.AddSingleton<IOptions<PatternDetectorOptions>>(Options.Create(new PatternDetectorOptions()));
                services.AddSingleton<IFeatureEngine>(new FeatureEngine(featureSettings));
                services.AddSingleton<IPatternDetectorFactory, PatternDetectorFactory>();
                services.AddSingleton<IMarketDatasetStore, ParquetMarketDatasetStore>();
                services.AddSingleton<ICandidateResearchService, CandidateResearchService>();
                services.AddSingleton<ForexRiskService>(); services.AddSingleton<ForexTradeSimulator>();
                services.AddSingleton<IDeterministicReplayService, DeterministicReplayService>();
            }
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
                case "fx-contract":
                    var contract = provider.GetRequiredService<ForexResearchContract>().Get(Required(options, "instrument-id"), Required(options, "strategy-id"));
                    Print(new { contract.Feature, contract.Pattern, contract.Label, contract.Profile.HoldingSeconds });
                    break;
                case "fx-replay":
                    Print(await provider.GetRequiredService<IDeterministicReplayService>().RunForexResearchAsync(new(
                        Required(options, "instrument-id"), ParseUtc(Required(options, "from")), ParseUtc(Required(options, "to")),
                        StrategyId: Required(options, "strategy-id"))));
                    break;
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

    private static async Task<int> AuditForexCollectionAsync(IReadOnlyDictionary<string, string> options)
    {
        var dataset = Required(options, "dataset");
        var file = Required(options, "config");
        var output = Optional(options, "output");
        if (!Path.IsPathFullyQualified(dataset) || !Directory.Exists(dataset)
            || !Path.IsPathFullyQualified(file) || !File.Exists(file))
            throw new ArgumentException("--dataset and --config must be existing absolute paths.");
        if (output != null && (!Path.IsPathFullyQualified(output) || File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output))))
            throw new ArgumentException("--output must be a new absolute report path in an existing directory.");
        var from = ParseUtc(Required(options, "from")); var to = ParseUtc(Required(options, "to"));
        if (from >= to || to - from > TimeSpan.FromDays(7)) throw new ArgumentException("Audit range must be positive and at most seven days.");
        var instrument = Required(options, "instrument-id");
        // This branch deliberately has no database connection or application DI container.
        var config = new ConfigurationBuilder().AddJsonFile(file).Build();
        var aggregates = config.GetSection("ShortAggregates").Get<ShortAggregateSettings>() ?? new();
        aggregates.IntervalsSeconds = config.GetSection("ShortAggregates:IntervalsSeconds").Get<int[]>() ?? aggregates.IntervalsSeconds;
        var store = new ParquetMarketDatasetStore(Options.Create(new DatasetStorageSettings { RootPath = dataset }),
            new SystemClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetMarketDatasetStore>.Instance);
        var verified = await store.VerifyAsync();
        if (!verified.IsValid) throw new InvalidDataException("Dataset integrity verification failed.");
        var manifest = await store.GetManifestAsync();
        var selectedRows = manifest.Files.Where(x => x.InstrumentId == instrument && x.DataType is "bid" or "ask"
            && x.MaximumEventTimeUtc >= from && x.MinimumEventTimeUtc < to).Sum(x => (long)x.RowCount);
        if (selectedRows > 200_000) throw new ArgumentException("Selected files exceed 200,000 events; select a smaller audit range.");
        var rows = await store.ReadAsync(new DatasetQuery(instrument, from, to, ["bid", "ask"]));
        var after = await store.VerifyAsync();
        if (!after.IsValid || verified.ManifestSha256 != after.ManifestSha256)
            throw new InvalidDataException("Dataset changed during audit; use a stable snapshot and retry.");
        var audit = ForexCollectionAudit.Analyze(rows, config.GetSection("TradingSettings").Get<TradingSettings>() ?? new(),
            aggregates, config.GetSection("CanonicalFeatures").Get<CanonicalFeatureSettings>() ?? new(), instrument, from, to);
        var report = new
        {
            canSendOrders = false, verification = verified, audit,
            limitations = new[] {
                "Offline receive-clock reconstruction, not a live worker scheduling or persisted signal audit.",
                "Only complete session-open windows available by ToUtc are counted; no empty bars are filled.",
                "Missing-window reason counts may overlap. A window without quotes does not prove broker data loss.",
                "Callback session changes reset features. Explicit live resets without new callbacks are not present in the quote dataset.",
                "26 bars and price feature readiness are separate measurements, not strategy/model approval or evidence of trading readiness."
            }
        };
        if (output != null)
        {
            await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, report, JsonOptions);
        }
        Print(report);
        return 0;
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
        Commands require --db ABSOLUTE_SQLITE_PATH except fx-collection-audit; optional --config APPSETTINGS_JSON overrides TradingBot.Web/appsettings.json.

        migrate
        fx-replay --dataset ABSOLUTE_PARQUET_DIRECTORY --instrument-id ID --strategy-id ID --from UTC --to UTC
        fx-contract --instrument-id ID --strategy-id ID
        fx-paper-preflight --config JSON_PATH --instrument-id ID --strategy-id ID [--output NEW_ABSOLUTE_REPORT_PATH]
        fx-collection-audit --config ABSOLUTE_JSON_PATH --dataset ABSOLUTE_PARQUET_DIRECTORY --instrument-id ID --from UTC --to UTC [--output NEW_ABSOLUTE_REPORT_PATH]
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
