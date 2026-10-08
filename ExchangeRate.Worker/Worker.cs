using System.Net.Http.Json;
using ExchangeRate.Business.Services;
using ExchangeRate.Data;
using Microsoft.EntityFrameworkCore;

namespace ExchangeRate.Worker;

public class Worker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<Worker> logger,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("Worker:IntervalSeconds", 5);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("Exchange rate worker starting. Interval: {Interval}s", intervalSeconds);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ExchangeRateDbContext>();
            await db.Database.MigrateAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await GenerateAndPublishRatesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to generate exchange rates.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Exchange rate worker stopping.");
    }

    private async Task GenerateAndPublishRatesAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExchangeRateService>();

        var batch = await service.GenerateAndSaveBatchAsync(stoppingToken);

        logger.LogInformation(
            "Saved {Count} exchange rates at {Time}: {Rates}",
            batch.Count,
            DateTime.Now,
            string.Join(", ", batch.Select(r => $"{r.CurrencyCode}={r.SellRate}")));

        // Push to the web app so connected browsers update in real time. A failure here must not
        // affect persistence (the data is already saved), so it is only logged.
        try
        {
            var client = httpClientFactory.CreateClient("WebPublisher");
            using var response = await client.PostAsJsonAsync("api/rates/publish", batch, stoppingToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish rates to the web app.");
        }
    }
}
