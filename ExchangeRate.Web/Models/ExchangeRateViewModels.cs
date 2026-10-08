using ExchangeRate.Data.Models;

namespace ExchangeRate.Web.Models;

public class ExchangeRateListViewModel
{
    public DateTime? FilterDate { get; set; }
    public string? FilterCurrency { get; set; }
    public List<ExchangeRateRecord> Rates { get; set; } = new();
    public List<string> AvailableCurrencies { get; set; } = new();
}

public class ExchangeRateStatisticsRow
{
    public DateOnly Date { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public decimal AverageBuyRate { get; set; }
    public decimal AverageSellRate { get; set; }
    public int SampleCount { get; set; }
}

public class ExchangeRateStatisticsViewModel
{
    public DateTime? FilterDate { get; set; }
    public List<ExchangeRateStatisticsRow> Statistics { get; set; } = new();
}
