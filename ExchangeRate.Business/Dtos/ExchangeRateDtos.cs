namespace ExchangeRate.Business.Dtos;

public record ExchangeRateDto(
    string CurrencyCode,
    string BaseCurrency,
    decimal BuyRate,
    decimal SellRate,
    DateTime Timestamp);

public record ExchangeRateStatisticsDto(
    DateOnly Date,
    string CurrencyCode,
    decimal AverageBuyRate,
    decimal AverageSellRate,
    int SampleCount);
