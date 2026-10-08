using ExchangeRate.Data;
using ExchangeRate.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExchangeRate.Web.Controllers;

public class ExchangeRatesController(ExchangeRateDbContext db) : Controller
{
    public async Task<IActionResult> Index(DateTime? date, string? currency)
    {
        var query = db.ExchangeRates.AsNoTracking().AsQueryable();

        if (date.HasValue)
        {
            var start = date.Value.Date;
            var end = start.AddDays(1);
            query = query.Where(r => r.Timestamp >= start && r.Timestamp < end);
        }

        if (!string.IsNullOrWhiteSpace(currency))
        {
            query = query.Where(r => r.CurrencyCode == currency);
        }

        var rates = await query
            .OrderByDescending(r => r.Timestamp)
            .Take(200)
            .ToListAsync();

        var currencies = await db.ExchangeRates
            .AsNoTracking()
            .Select(r => r.CurrencyCode)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        var viewModel = new ExchangeRateListViewModel
        {
            FilterDate = date,
            FilterCurrency = currency,
            Rates = rates,
            AvailableCurrencies = currencies,
        };

        return View(viewModel);
    }

    public async Task<IActionResult> Latest()
    {
        var latestPerCurrency = db.ExchangeRates
            .AsNoTracking()
            .GroupBy(r => r.CurrencyCode)
            .Select(g => new { CurrencyCode = g.Key, MaxTimestamp = g.Max(r => r.Timestamp) });

        var latest = await db.ExchangeRates
            .AsNoTracking()
            .Join(latestPerCurrency,
                r => new { r.CurrencyCode, Timestamp = r.Timestamp },
                m => new { m.CurrencyCode, Timestamp = m.MaxTimestamp },
                (r, m) => r)
            .OrderBy(r => r.CurrencyCode)
            .ToListAsync();

        return View(latest);
    }

    public async Task<IActionResult> Statistics(DateTime? date)
    {
        var query = db.ExchangeRates.AsNoTracking().AsQueryable();

        if (date.HasValue)
        {
            var start = date.Value.Date;
            var end = start.AddDays(1);
            query = query.Where(r => r.Timestamp >= start && r.Timestamp < end);
        }

        var grouped = await query
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
            .ToListAsync();

        var viewModel = new ExchangeRateStatisticsViewModel
        {
            FilterDate = date,
            Statistics = grouped.Select(g => new ExchangeRateStatisticsRow
            {
                Date = DateOnly.FromDateTime(g.Date),
                CurrencyCode = g.CurrencyCode,
                AverageBuyRate = Math.Round(g.AvgBuy, 2),
                AverageSellRate = Math.Round(g.AvgSell, 2),
                SampleCount = g.Count,
            }).ToList(),
        };

        return View(viewModel);
    }
}
