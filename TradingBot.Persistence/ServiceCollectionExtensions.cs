using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TradingBot.Persistence
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("TradingBot") ?? configuration["DatabaseSettings:ConnectionString"] ?? "Data Source=data/trading.db";

            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (builder.DefaultTimeout < 5)
            {
                builder.DefaultTimeout = 5;
            }

            connectionString = builder.ToString();
            var dataSource = builder.DataSource;
            if (!string.IsNullOrWhiteSpace(dataSource) && !dataSource.Equals(":memory:", System.StringComparison.OrdinalIgnoreCase))
            {
                var dir = System.IO.Path.GetDirectoryName(dataSource);
                if (!string.IsNullOrEmpty(dir))
                    System.IO.Directory.CreateDirectory(dir);
            }

            // Use IDbContextFactory to avoid injecting long-lived DbContext in background services.
            // SQLite still has a single-writer model; WAL plus a short command timeout keeps
            // expected read/write overlap from becoming immediate "database is locked" failures.
            services.AddDbContextFactory<TradingBotDbContext>(options =>
                options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(5)));

            // Register a scoped TradingBotDbContext that is created from the factory per scope
            services.AddScoped<TradingBotDbContext>(sp => sp.GetRequiredService<IDbContextFactory<TradingBotDbContext>>().CreateDbContext());

            // Add a hosted service to apply migrations at startup
            services.AddHostedService<MigrationHostedService>();
            return services;
        }
    }
}
