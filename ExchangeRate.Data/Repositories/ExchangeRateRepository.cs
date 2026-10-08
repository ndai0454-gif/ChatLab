using ExchangeRate.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace ExchangeRate.Data.Repositories;

public class ExchangeRateRepository(ExchangeRateDbContext db) : IExchangeRateRepository
{
    public Task<List<ExchangeRateRecord>> SearchAsync(DateTime? date, string? currency, int take, CancellationToken ct = default)
    {
        var query = FilterByDate(db.ExchangeRates.AsNoTracking(), date);

        if (!string.IsNullOrWhiteSpace(currency))
        {
            query = query.Where(r => r.CurrencyCode == currency);
        }

        return query.OrderByDescending(r => r.Timestamp).Take(take).ToListAsync(ct);
    }

    public Task<List<string>> GetCurrenciesAsync(CancellationToken ct = default) =>
        db.ExchangeRates.AsNoTracking()
            .Select(r => r.CurrencyCode)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

    public Task<List<ExchangeRateRecord>> GetLatestPerCurrencyAsync(CancellationToken ct = default)
    {
        var maxPerCurrency = db.ExchangeRates
            .AsNoTracking()
            .GroupBy(r => r.CurrencyCode)
            .Select(g => new { CurrencyCode = g.Key, MaxTimestamp = g.Max(r => r.Timestamp) });

        return db.ExchangeRates
            .AsNoTracking()
            .Join(maxPerCurrency,
                r => new { r.CurrencyCode, r.Timestamp },
                m => new { m.CurrencyCode, Timestamp = m.MaxTimestamp },
                (r, m) => r)
            .OrderBy(r => r.CurrencyCode)
            .ToListAsync(ct);
    }

    public async Task<List<RateAggregate>> GetStatisticsAsync(DateTime? date, CancellationToken ct = default)
    {
        var rows = await FilterByDate(db.ExchangeRates.AsNoTracking(), date)
            .GroupBy(r => new { r.CurrencyCode, Date = r.Timestamp.Date })
            .Select(g => new
            {
                g.Key.CurrencyCode,
                g.Key.Date,
                AvgBuy = g.Average(r => r.BuyRate),
                AvgSell = g.Average(r => r.SellRate),
                Count = g.Count(),
            })
            .OrderByDescending(g => g.Date)
            .ThenBy(g => g.CurrencyCode)
            .ToListAsync(ct);

        return rows.Select(g => new RateAggregate(g.CurrencyCode, g.Date, g.AvgBuy, g.AvgSell, g.Count)).ToList();
    }

    public async Task AddRangeAsync(IEnumerable<ExchangeRateRecord> records, CancellationToken ct = default)
    {
        db.ExchangeRates.AddRange(records);
        await db.SaveChangesAsync(ct);
    }

    private static IQueryable<ExchangeRateRecord> FilterByDate(IQueryable<ExchangeRateRecord> query, DateTime? date)
    {
        if (!date.HasValue) return query;
        var start = date.Value.Date;
        var end = start.AddDays(1);
        return query.Where(r => r.Timestamp >= start && r.Timestamp < end);
    }
}
