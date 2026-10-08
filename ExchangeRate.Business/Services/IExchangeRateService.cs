using ExchangeRate.Business.Dtos;

namespace ExchangeRate.Business.Services;

public interface IExchangeRateService
{
    Task<List<ExchangeRateDto>> SearchAsync(DateTime? date, string? currency, CancellationToken ct = default);
    Task<List<string>> GetCurrenciesAsync(CancellationToken ct = default);
    Task<List<ExchangeRateDto>> GetLatestAsync(CancellationToken ct = default);
    Task<List<ExchangeRateStatisticsDto>> GetStatisticsAsync(DateTime? date, CancellationToken ct = default);

    /// <summary>Generates a random batch of rates, persists it and returns what was saved.</summary>
    Task<List<ExchangeRateDto>> GenerateAndSaveBatchAsync(CancellationToken ct = default);
}
