using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using TradingBot.Persistence;
using TradingBot.Infrastructure;
using NLog.Web;
using NLog;
using TradingBot.Application.Configuration;
using Microsoft.Extensions.Options;
using TradingBot.Application.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Web.HealthChecks;

var logDirectory = ResolveLogDirectory();
Directory.CreateDirectory(logDirectory);
Environment.SetEnvironmentVariable("TRADINGBOT_LOG_DIR", logDirectory);

var logger = LogManager.Setup()
    .LoadConfigurationFromFile(Path.Combine(AppContext.BaseDirectory, "nlog.config"), optional: false)
    .GetCurrentClassLogger();
try
{
    var builder = WebApplication.CreateBuilder(args);

    // Use NLog as the logging provider
    builder.Logging.ClearProviders();
    builder.Host.UseNLog();
    builder.Host.ConfigureHostOptions(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });

    // Add services to the container.
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();

    // Bind configuration sections to strongly typed settings
    builder.Services.AddOptions<TradingSettings>()
        .Bind(builder.Configuration.GetSection("TradingSettings"))
        .Validate(settings => settings.GetValidationErrors().Count == 0, "TradingSettings instrument configuration is invalid. Check InstrumentId, Symbol, market-data timeframes and duplicate values.")
        .Validate(settings => settings.MinimumPatternQualityForAiAnalysis is >= 0d and <= 1d, "TradingSettings:MinimumPatternQualityForAiAnalysis must be between 0 and 1.")
        .Validate(settings => settings.MinimumAiConfidence is >= 0m and <= 1m, "TradingSettings:MinimumAiConfidence must be between 0 and 1.")
        .Validate(settings => settings.MinimumPatternQuality is >= 0m and <= 1m, "TradingSettings:MinimumPatternQuality must be between 0 and 1.")
        .Validate(settings => settings.MinimumRewardRiskRatio > 0m, "TradingSettings:MinimumRewardRiskRatio must be greater than zero.")
        .Validate(settings => settings.MaximumSpread >= 0m, "TradingSettings:MaximumSpread cannot be negative.")
        .Validate(settings => settings.TradingStartHourUtc is >= 0 and <= 23, "TradingSettings:TradingStartHourUtc must be 0-23.")
        .Validate(settings => settings.TradingEndHourUtc is >= 0 and <= 23, "TradingSettings:TradingEndHourUtc must be 0-23.")
        .Validate(settings => settings.OperatingMode != TradingBot.Domain.Enums.TradingOperatingMode.LiveTrading || settings.LiveTradingExplicitlyEnabled, "LiveTrading requires TradingSettings:LiveTradingExplicitlyEnabled=true.")
        .ValidateOnStart();

    builder.Services.AddOptions<IbkrSettings>()
        .Bind(builder.Configuration.GetSection("IbkrSettings"))
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.Host), "IbkrSettings:Host is required.")
        .Validate(settings => settings.ClientId >= 0, "IbkrSettings:ClientId cannot be negative.")
        .Validate(settings => settings.Paper.Port is > 0 and <= 65535, "IbkrSettings:Paper:Port must be 1-65535.")
        .Validate(settings => settings.Live.Port is > 0 and <= 65535, "IbkrSettings:Live:Port must be 1-65535.")
        .Validate(settings => settings.Paper.Port != settings.Live.Port, "IBKR paper and live ports must be different.")
        .ValidateOnStart();

    builder.Services.AddOptions<HistoricalBackfillSettings>()
        .Bind(builder.Configuration.GetSection("HistoricalBackfill"))
        .Validate(settings => settings.GetValidationErrors().Count == 0, "HistoricalBackfill configuration is invalid. Check lookback, segment, pacing and retry values.")
        .ValidateOnStart();

    builder.Services.AddOptions<DatasetStorageSettings>()
        .Bind(builder.Configuration.GetSection("DatasetStorage"))
        .Validate(settings => settings.GetValidationErrors().Count == 0, "DatasetStorage configuration is invalid. Check root path, queue, batch, flush interval and schema version.")
        .ValidateOnStart();

    builder.Services.AddOptions<MarketDataCollectionSettings>()
        .Bind(builder.Configuration.GetSection("MarketDataCollection"))
        .Validate(settings => settings.GetValidationErrors().Count == 0, "MarketDataCollection configuration is invalid. Check heartbeat, reconnect and gap-fill values.")
        .ValidateOnStart();

    builder.Services.AddOptions<OpenAiSettings>()
        .Bind(builder.Configuration.GetSection("OpenAiSettings"))
        .Validate(settings => Uri.TryCreate(settings.ResponsesEndpoint, UriKind.Absolute, out _), "OpenAiSettings:ResponsesEndpoint must be an absolute URL.")
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.ApiKey) || !string.IsNullOrWhiteSpace(settings.ApiKeyName), "OpenAiSettings:ApiKey or OpenAiSettings:ApiKeyName is required.")
        .Validate(settings => settings.TimeoutSeconds > 0, "OpenAiSettings:TimeoutSeconds must be greater than zero.")
        .Validate(settings => settings.MaxRetries >= 0, "OpenAiSettings:MaxRetries cannot be negative.")
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.Analyzer.Model), "OpenAiSettings:Analyzer:Model is required.")
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.Analyzer.PromptVersion), "OpenAiSettings:Analyzer:PromptVersion is required.")
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.Critic.Model), "OpenAiSettings:Critic:Model is required.")
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.Critic.PromptVersion), "OpenAiSettings:Critic:PromptVersion is required.")
        .Validate(settings => settings.MarketContext.OneMinuteCandles > 0, "OpenAiSettings:MarketContext:OneMinuteCandles must be greater than zero.")
        .Validate(settings => settings.MarketContext.FiveMinuteCandles > 0, "OpenAiSettings:MarketContext:FiveMinuteCandles must be greater than zero.")
        .Validate(settings => settings.MarketContext.FifteenMinuteCandles > 0, "OpenAiSettings:MarketContext:FifteenMinuteCandles must be greater than zero.")
        .Validate(settings => settings.Pricing.Analyzer.InputPricePerMillionTokens >= 0m, "OpenAiSettings:Pricing:Analyzer:InputPricePerMillionTokens cannot be negative.")
        .Validate(settings => settings.Pricing.Analyzer.OutputPricePerMillionTokens >= 0m, "OpenAiSettings:Pricing:Analyzer:OutputPricePerMillionTokens cannot be negative.")
        .Validate(settings => settings.Pricing.Critic.InputPricePerMillionTokens >= 0m, "OpenAiSettings:Pricing:Critic:InputPricePerMillionTokens cannot be negative.")
        .Validate(settings => settings.Pricing.Critic.OutputPricePerMillionTokens >= 0m, "OpenAiSettings:Pricing:Critic:OutputPricePerMillionTokens cannot be negative.")
        .ValidateOnStart();

    builder.Services.AddOptions<RiskSettings>()
        .Bind(builder.Configuration.GetSection("RiskSettings"))
        .Validate(settings => settings.MaximumPositionValue > 0m, "RiskSettings:MaximumPositionValue must be greater than zero.")
        .Validate(settings => settings.MaximumRiskPerTrade > 0m, "RiskSettings:MaximumRiskPerTrade must be greater than zero.")
        .Validate(settings => settings.MaximumDailyLoss > 0m, "RiskSettings:MaximumDailyLoss must be greater than zero.")
        .Validate(settings => settings.MaximumLeverage >= 1m, "RiskSettings:MaximumLeverage must be at least 1.")
        .Validate(settings => settings.MaximumOpenPositions >= 1, "RiskSettings:MaximumOpenPositions must be at least 1.")
        .Validate(settings => settings.MaximumConsecutiveLosses >= 1, "RiskSettings:MaximumConsecutiveLosses must be at least 1.")
        .ValidateOnStart();

    builder.Services.AddOptions<ExitStrategySettings>()
        .Bind(builder.Configuration.GetSection("ExitStrategy"))
        .Validate(settings => settings.BreakEvenTriggerR >= 0m, "ExitStrategy:BreakEvenTriggerR cannot be negative.")
        .Validate(settings => settings.BreakEvenOffsetR >= 0m, "ExitStrategy:BreakEvenOffsetR cannot be negative.")
        .Validate(settings => settings.TrailingActivationR >= settings.BreakEvenTriggerR, "ExitStrategy:TrailingActivationR must be greater than or equal to BreakEvenTriggerR.")
        .Validate(settings => settings.TrailingAtrMultiplier > 0m, "ExitStrategy:TrailingAtrMultiplier must be greater than zero.")
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.TrailingAtrTimeframe), "ExitStrategy:TrailingAtrTimeframe is required.")
        .Validate(settings => settings.MaximumHoldingMinutes == null || settings.MaximumHoldingMinutes > 0, "ExitStrategy:MaximumHoldingMinutes must be null or greater than zero.")
        .ValidateOnStart();

    builder.Services.AddOptions<DatabaseSettings>()
        .Bind(builder.Configuration.GetSection("DatabaseSettings"))
        .Validate(settings => !string.IsNullOrWhiteSpace(settings.ConnectionString), "DatabaseSettings:ConnectionString is required.")
        .ValidateOnStart();

    builder.Services.AddOptions<TradingBot.Infrastructure.Options.PatternDetectorOptions>()
        .Bind(builder.Configuration.GetSection("PatternDetector"))
        .Validate(settings => settings.MinimumTradeSetupQuality is >= 0m and <= 1m, "PatternDetector:MinimumTradeSetupQuality must be between 0 and 1.")
        .Validate(settings => settings.AiAnalysisTimeframes?.Any(timeframe => !string.IsNullOrWhiteSpace(timeframe)) == true, "PatternDetector:AiAnalysisTimeframes must contain at least one timeframe.")
        .Validate(settings => settings.MinimumOneMinuteCandlesForTradeSetup >= 1, "PatternDetector:MinimumOneMinuteCandlesForTradeSetup must be at least 1.")
        .Validate(settings => settings.MinimumFiveMinuteCandlesForTradeSetup >= 1, "PatternDetector:MinimumFiveMinuteCandlesForTradeSetup must be at least 1.")
        .Validate(settings => settings.MinimumFifteenMinuteCandlesForTradeSetup >= 1, "PatternDetector:MinimumFifteenMinuteCandlesForTradeSetup must be at least 1.")
        .ValidateOnStart();

    builder.Services.AddHealthChecks()
        .AddCheck("web", () => HealthCheckResult.Healthy("Web application is running."), tags: new[] { "live" })
        .AddCheck<DatabaseHealthCheck>("database", tags: new[] { "dependency", "ready" })
        .AddCheck<IbkrConnectionHealthCheck>("ibkr", tags: new[] { "dependency", "trading-ready" })
        .AddCheck<TradingEngineReadinessHealthCheck>("trading-engine", tags: new[] { "trading-ready" })
        .AddCheck<OpenAiAvailabilityHealthCheck>("openai", tags: new[] { "dependency", "analysis" });

    // Persistence and infrastructure registration
    builder.Services.AddPersistence(builder.Configuration);
    builder.Services.AddInfrastructure();

    var app = builder.Build();

    app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Application stopping; graceful shutdown requested."));
    app.Lifetime.ApplicationStopped.Register(() => app.Logger.LogInformation("Application stopped."));

    // Startup logging
    var env = app.Environment.EnvironmentName;
    var dbSettings = app.Services.GetService(typeof(IOptions<DatabaseSettings>)) as IOptions<DatabaseSettings>;
    var tradingSettings = app.Services.GetService(typeof(IOptions<TradingSettings>)) as IOptions<TradingSettings>;

    app.Logger.LogInformation("Application started");
    app.Logger.LogInformation("Environment: {env}", env);
    if (dbSettings is not null)
    {
        app.Logger.LogInformation("Database connection: {db}", dbSettings.Value.ConnectionString);
    }
    if (tradingSettings is not null)
    {
        app.Logger.LogInformation("Trading enabled: {enabled}", tradingSettings.Value.Enabled);
        app.Logger.LogWarning("Trading operating mode selected at startup: {mode}; LiveTradingExplicitlyEnabled={liveEnabled}", tradingSettings.Value.OperatingMode, tradingSettings.Value.LiveTradingExplicitlyEnabled);
    }

    // Configure the HTTP request pipeline.
    app.UseExceptionHandler("/Error");
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();

    app.UseStaticFiles();

    app.UseRouting();

    // Health endpoints: web liveness is separate from dependency/trading readiness.
    app.MapGet("/api/health", () => Results.Ok(new { status = "Healthy", component = "web", utc = System.DateTime.UtcNow }));
    app.MapHealthChecks("/api/health/details", HealthCheckResponseWriter.AlwaysOkOptions());
    app.MapHealthChecks("/api/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthCheckResponseWriter.WriteJsonAsync
    });
    app.MapHealthChecks("/api/health/trading-ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("trading-ready"),
        ResponseWriter = HealthCheckResponseWriter.WriteJsonAsync
    });
    app.MapHealthChecks("/api/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live"),
        ResponseWriter = HealthCheckResponseWriter.WriteJsonAsync
    });
    app.MapGet("/api/reconciliation/status", (ITradingEngineStatusService statusService) => Results.Ok(statusService.Current));
    app.MapGet("/api/instruments", async (IInstrumentRegistryService registry, CancellationToken cancellationToken) =>
        Results.Ok(await registry.GetAllAsync(cancellationToken)));
    app.MapGet("/api/market-data/streams", async (IMarketDataQualityService quality, CancellationToken cancellationToken) =>
        Results.Ok(await quality.GetStreamsAsync(cancellationToken)));
    app.MapGet("/api/market-data/incidents", async (int? count, IMarketDataQualityService quality, CancellationToken cancellationToken) =>
        Results.Ok(await quality.GetRecentIncidentsAsync(count ?? 100, cancellationToken)));
    app.MapGet("/api/market-data/latest", (ILatestMarketDataService latest) => Results.Ok(latest.GetAll()));
    app.MapGet("/api/market-data/collection", (IMarketDataCollectionStatusService collection) => Results.Ok(collection.GetAll()));
    app.MapGet("/api/datasets/manifest", async (IMarketDatasetStore datasets, CancellationToken cancellationToken) =>
        Results.Ok(await datasets.GetManifestAsync(cancellationToken)));
    app.MapGet("/api/datasets/verify", async (IMarketDatasetStore datasets, CancellationToken cancellationToken) =>
    {
        var result = await datasets.VerifyAsync(cancellationToken);
        return result.IsValid ? Results.Ok(result) : Results.Problem(
            title: "Dataset integrity verification failed.",
            detail: string.Join(" ", result.Errors),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    });
    app.MapGet("/api/historical-backfill/jobs", async (IHistoricalBackfillService backfill, CancellationToken cancellationToken) =>
        Results.Ok(await backfill.GetJobsAsync(cancellationToken)));
    app.MapGet("/api/historical-backfill/gaps", async (string? instrumentId, IHistoricalBackfillService backfill, CancellationToken cancellationToken) =>
        Results.Ok(await backfill.GetGapsAsync(instrumentId, cancellationToken)));
    if (TradingBot.Web.DevelopmentEndpointGuard.ShouldExposeDevelopmentEndpoints(app.Environment))
    {
        app.MapGet("/api/dev/openai-smoke-test", (IOptions<OpenAiSettings> openAiOptions, IOpenAiApiKeyProvider apiKeyProvider) =>
        {
            var settings = openAiOptions.Value;
            var apiKeyResolution = apiKeyProvider.Resolve();

            return Results.Ok(new
            {
                available = true,
                environment = app.Environment.EnvironmentName,
                message = "OpenAI smoke test endpoint is available. Send POST to this same URL to run the live Responses API smoke test.",
                methodRequired = "POST",
                apiKeyEnvironmentVariable = settings.ApiKeyName,
                apiKeyConfigured = apiKeyResolution.IsConfigured,
                apiKeySource = apiKeyResolution.Source,
                apiKeyConfigurationError = apiKeyResolution.IsConfigured ? string.Empty : apiKeyResolution.FailureReason,
                analyzerModel = settings.Analyzer.Model,
                analyzerPromptVersion = settings.Analyzer.PromptVersion,
                criticModel = settings.Critic.Model,
                criticPromptVersion = settings.Critic.PromptVersion,
                responsesEndpoint = settings.ResponsesEndpoint
            });
        });

        app.MapPost("/api/dev/openai-smoke-test", async (IOpenAiSmokeTestService smokeTestService, CancellationToken cancellationToken) =>
        {
            var result = await smokeTestService.RunAsync(cancellationToken).ConfigureAwait(false);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapGet("/api/dev/ibkr-diagnostics", async (
            IServiceProvider serviceProvider,
            IOptions<IbkrSettings> ibkrOptions,
            IOptions<TradingSettings> tradingOptions,
            CancellationToken cancellationToken) =>
        {
            var ibkrSettings = ibkrOptions.Value;
            var tradingSettings = tradingOptions.Value;
            var endpointPort = ibkrSettings.GetPort(tradingSettings.OperatingMode);
            var adapter = serviceProvider.GetService<TradingBot.Infrastructure.Interfaces.IIbkrAdapter>();
            var connection = serviceProvider.GetService<IIbkrConnectionService>();
            var apiDllInOutput = Path.Combine(AppContext.BaseDirectory, "CSharpAPI.dll");
            const string defaultApiDllPath = @"C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\CSharpAPI.dll";

            var socketReachable = false;
            string? socketError = null;
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                await tcpClient.ConnectAsync(ibkrSettings.Host, endpointPort, timeoutCts.Token).ConfigureAwait(false);
                socketReachable = tcpClient.Connected;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException)
            {
                socketError = ex.Message;
            }

            return Results.Ok(new
            {
                environment = app.Environment.EnvironmentName,
                operatingMode = tradingSettings.OperatingMode.ToString(),
                tradingEnabled = tradingSettings.Enabled,
                endpoint = $"{ibkrSettings.Host}:{endpointPort}",
                hostType = ibkrSettings.HostType.ToString(),
                clientId = ibkrSettings.ClientId,
                configuredPaperAccount = string.IsNullOrWhiteSpace(ibkrSettings.PaperAccountId) ? "" : "configured",
                configuredLiveAccount = string.IsNullOrWhiteSpace(ibkrSettings.AccountId) ? "" : "configured",
                adapterType = adapter?.GetType().FullName ?? "not registered",
                connectionServiceType = connection?.GetType().FullName ?? "not registered",
                connectionStatus = connection?.Status.ToString() ?? "Unavailable",
                csharpApiDllCopiedToOutput = System.IO.File.Exists(apiDllInOutput),
                csharpApiDllDefaultPathExists = System.IO.File.Exists(defaultApiDllPath),
                socketReachable,
                socketError
            });
        });

        app.MapGet("/api/dev/dashboard-snapshot", async (IDashboardService dashboardService, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await dashboardService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem(
                    title: "Dashboard snapshot failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    app.MapBlazorHub();
    app.MapFallbackToPage("/_Host");

    app.Run();
}
catch (System.Exception ex)
{
    // NLog: catch setup errors
    logger.Error(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    LogManager.Shutdown();
}

static string ResolveLogDirectory()
{
    var currentDirectory = Directory.GetCurrentDirectory();
    var currentProjectFile = Path.Combine(currentDirectory, "TradingBot.Web.csproj");
    if (File.Exists(currentProjectFile))
    {
        return Path.Combine(currentDirectory, "logs");
    }

    var nestedProjectDirectory = Path.Combine(currentDirectory, "TradingBot.Web");
    if (File.Exists(Path.Combine(nestedProjectDirectory, "TradingBot.Web.csproj")))
    {
        return Path.Combine(nestedProjectDirectory, "logs");
    }

    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TradingBot.Web.csproj")))
        {
            return Path.Combine(directory.FullName, "logs");
        }

        directory = directory.Parent;
    }

    return Path.Combine(currentDirectory, "logs");
}
