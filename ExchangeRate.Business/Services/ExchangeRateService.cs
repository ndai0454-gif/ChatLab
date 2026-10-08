using ExchangeRate.Business.Dtos;
using ExchangeRate.Data;
using ExchangeRate.Data.Models;
using ExchangeRate.Data.Repositories;

namespace ExchangeRate.Business.Services;

public class ExchangeRateService(IExchangeRateRepository repository) : IExchangeRateService
{
    private const int MaxListRows = 200;

    public async Task<List<ExchangeRateDto>> SearchAsync(DateTime? date, string? currency, CancellationToken ct = default) =>
        (await repository.SearchAsync(date, currency, MaxListRows, ct)).Select(ToDto).ToList();

    public Task<List<string>> GetCurrenciesAsync(CancellationToken ct = default) =>
        repository.GetCurrenciesAsync(ct);

    public async Task<List<ExchangeRateDto>> GetLatestAsync(CancellationToken ct = default) =>
        (await repository.GetLatestPerCurrencyAsync(ct)).Select(ToDto).ToList();

    public async Task<List<ExchangeRateStatisticsDto>> GetStatisticsAsync(DateTime? date, CancellationToken ct = default) =>
        (await repository.GetStatisticsAsync(date, ct))
            .Select(a => new ExchangeRateStatisticsDto(
                DateOnly.FromDateTime(a.Date),
                a.CurrencyCode,
                Math.Round(a.AvgBuy, 2),
                Math.Round(a.AvgSell, 2),
                a.Count))
            .ToList();

    public async Task<List<ExchangeRateDto>> GenerateAndSaveBatchAsync(CancellationToken ct = default)
    {
        var batch = RandomRateGenerator.GenerateBatch(DateTime.Now).ToList();
        await repository.AddRangeAsync(batch, ct);
        return batch.Select(ToDto).ToList();
    }

    private static ExchangeRateDto ToDto(ExchangeRateRecord r) =>
        new(r.CurrencyCode, r.BaseCurrency, r.BuyRate, r.SellRate, r.Timestamp);
}
