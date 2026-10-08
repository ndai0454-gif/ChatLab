using System.ComponentModel.DataAnnotations;

namespace ExchangeRate.Data.Models;

public class ExchangeRateRecord
{
    public int Id { get; set; }

    [MaxLength(10)]
    public string CurrencyCode { get; set; } = string.Empty;

    [MaxLength(10)]
    public string BaseCurrency { get; set; } = "VND";

    public decimal BuyRate { get; set; }

    public decimal SellRate { get; set; }

    public DateTime Timestamp { get; set; }
}
