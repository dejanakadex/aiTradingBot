using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TradingBot.Persistence.Migrations;

[DbContext(typeof(TradingBotDbContext))]
[Migration("20261025140000_AllowUnavailableCandleVolume")]
public sealed class AllowUnavailableCandleVolume : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "Candles_Fx4" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Candles" PRIMARY KEY AUTOINCREMENT,
            "InstrumentId" TEXT NOT NULL, "Symbol" TEXT NOT NULL, "Timeframe" INTEGER NOT NULL,
            "TimestampUtc" TEXT NOT NULL, "ReceivedTimeUtc" TEXT NOT NULL, "Source" TEXT NOT NULL,
            "IsFinal" INTEGER NOT NULL, "QualityStatus" INTEGER NOT NULL,
            "Open" TEXT NOT NULL, "High" TEXT NOT NULL, "Low" TEXT NOT NULL, "Close" TEXT NOT NULL,
            "Volume" TEXT NULL
        );
        INSERT INTO "Candles_Fx4" (Id, InstrumentId, Symbol, Timeframe, TimestampUtc, ReceivedTimeUtc, Source,
            IsFinal, QualityStatus, Open, High, Low, Close, Volume)
        SELECT Id, InstrumentId, Symbol, Timeframe, TimestampUtc, ReceivedTimeUtc, Source,
            IsFinal, QualityStatus, Open, High, Low, Close, Volume FROM "Candles";
        DROP TABLE "Candles";
        ALTER TABLE "Candles_Fx4" RENAME TO "Candles";
        CREATE UNIQUE INDEX "IX_Candles_Symbol_Timeframe_TimestampUtc" ON "Candles" (Symbol, Timeframe, TimestampUtc);
        CREATE INDEX "IX_Candles_InstrumentId" ON "Candles" (InstrumentId);
        CREATE INDEX "IX_Candles_ReceivedTimeUtc" ON "Candles" (ReceivedTimeUtc);
        CREATE INDEX "IX_Candles_QualityStatus" ON "Candles" (QualityStatus);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Rollback would lose unavailable-volume semantics. Restore a pre-migration backup instead.");
}
