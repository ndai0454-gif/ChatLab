namespace ExchangeRate.Desktop.Services;

public interface IRateQueryService
{
    Task<RateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
