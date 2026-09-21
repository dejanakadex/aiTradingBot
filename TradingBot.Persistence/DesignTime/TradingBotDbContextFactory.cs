using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradingBot.Persistence.DesignTime
{
    public class TradingBotDbContextFactory : IDesignTimeDbContextFactory<TradingBotDbContext>
    {
        public TradingBotDbContext CreateDbContext(string[] args)
        {
            var builder = new DbContextOptionsBuilder<TradingBotDbContext>();
            var connectionString = "Data Source=data/trading.db";
            builder.UseSqlite(connectionString);
            return new TradingBotDbContext(builder.Options);
        }
    }
}
