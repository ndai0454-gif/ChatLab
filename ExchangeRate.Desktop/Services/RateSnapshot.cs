using ExchangeRate.Data.Models;

namespace ExchangeRate.Desktop.Services;

public enum WorkerStatus
{
    Unknown,
    Running,
    Stopped,
}

public class RateSnapshot
{
    public List<ExchangeRateRecord> LatestPerCurrency { get; init; } = new();
    public WorkerStatus WorkerStatus { get; init; } = WorkerStatus.Unknown;
    public DateTime? LastDataTimestamp { get; init; }
}
