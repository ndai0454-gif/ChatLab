using ExchangeRate.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ExchangeRate.Desktop.Services;

public class RateQueryService(IDbContextFactory<ExchangeRateDbContext> dbContextFactory, IConfiguration configuration)
    : IRateQueryService
{
    public async Task<RateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var latestTimestamps = db.ExchangeRates
            .AsNoTracking()
            .GroupBy(r => r.CurrencyCode)
            .Select(g => new { CurrencyCode = g.Key, MaxTimestamp = g.Max(r => r.Timestamp) });

        var latestPerCurrency = await db.ExchangeRates
            .AsNoTracking()
            .Join(latestTimestamps,
                r => new { r.CurrencyCode, Timestamp = r.Timestamp },
                m => new { m.CurrencyCode, Timestamp = m.MaxTimestamp },
                (r, m) => r)
            .OrderBy(r => r.CurrencyCode)
            .ToListAsync(cancellationToken);

        var lastTimestamp = latestPerCurrency.Count > 0
            ? latestPerCurrency.Max(r => r.Timestamp)
            : (DateTime?)null;

        var staleAfterSeconds = configuration.GetValue("Monitor:WorkerStaleAfterSeconds", 15);
        var status = lastTimestamp is null
            ? WorkerStatus.Unknown
            : (DateTime.Now - lastTimestamp.Value).TotalSeconds <= staleAfterSeconds
                ? WorkerStatus.Running
                : WorkerStatus.Stopped;

        return new RateSnapshot
        {
            LatestPerCurrency = latestPerCurrency,
            WorkerStatus = status,
            LastDataTimestamp = lastTimestamp,
        };
    }
}
