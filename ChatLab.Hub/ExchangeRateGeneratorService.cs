using ChatLab.Hub;
using ExchangeRate.Business.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatLab.Hub;

public sealed class ExchangeRateGeneratorService(
    IServiceScopeFactory scopeFactory,
    IHubContext<ExchangeRatesHub> hub,
    IConfiguration configuration,
    ILogger<ExchangeRateGeneratorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("ExchangeRates:IntervalSeconds", 30);
        if (intervalSeconds < 1)
        {
            throw new InvalidOperationException("ExchangeRates:IntervalSeconds must be greater than zero.");
        }

        logger.LogInformation("Exchange rate generator starting. Interval: {Interval}s", intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IExchangeRateService>();
                var rates = await service.GenerateAndSaveBatchAsync(stoppingToken);
                await hub.Clients.All.SendAsync(ExchangeRatesHub.RatesUpdatedEvent, rates, stoppingToken);
                logger.LogInformation("Generated and published {RateCount} sample exchange rates.", rates.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to generate or publish exchange rates.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
