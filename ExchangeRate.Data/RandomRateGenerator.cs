using ExchangeRate.Data.Models;

namespace ExchangeRate.Data;

public static class RandomRateGenerator
{
    public static readonly IReadOnlyDictionary<string, (decimal Buy, decimal Sell)> BaseRates =
        new Dictionary<string, (decimal Buy, decimal Sell)>
        {
            ["USD"] = (24_950m, 25_350m),
            ["EUR"] = (26_800m, 27_300m),
            ["JPY"] = (160m, 168m),
            ["GBP"] = (31_200m, 31_800m),
            ["AUD"] = (16_300m, 16_700m),
        };

    public static IEnumerable<ExchangeRateRecord> GenerateBatch(DateTime timestamp)
    {
        foreach (var (currencyCode, (baseBuy, baseSell)) in BaseRates)
        {
            var fluctuation = (decimal)(Random.Shared.NextDouble() * 0.02 - 0.01); // +/-1%
            yield return new ExchangeRateRecord
            {
                CurrencyCode = currencyCode,
                BaseCurrency = "VND",
                BuyRate = Math.Round(baseBuy * (1 + fluctuation), 2),
                SellRate = Math.Round(baseSell * (1 + fluctuation), 2),
                Timestamp = timestamp,
            };
        }
    }
}
