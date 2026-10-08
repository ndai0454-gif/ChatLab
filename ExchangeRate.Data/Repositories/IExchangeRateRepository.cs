using ExchangeRate.Data.Models;

namespace ExchangeRate.Data.Repositories;

public record RateAggregate(string CurrencyCode, DateTime Date, decimal AvgBuy, decimal AvgSell, int Count);

public interface IExchangeRateRepository
{
    Task<List<ExchangeRateRecord>> SearchAsync(DateTime? date, string? currency, int take, CancellationToken ct = default);
    Task<List<string>> GetCurrenciesAsync(CancellationToken ct = default);
    Task<List<ExchangeRateRecord>> GetLatestPerCurrencyAsync(CancellationToken ct = default);
    Task<List<RateAggregate>> GetStatisticsAsync(DateTime? date, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<ExchangeRateRecord> records, CancellationToken ct = default);
}
