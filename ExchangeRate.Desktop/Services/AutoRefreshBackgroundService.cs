using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExchangeRate.Desktop.Services;

public class AutoRefreshBackgroundService(
    MainViewModel viewModel,
    IConfiguration configuration,
    ILogger<AutoRefreshBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue("Monitor:AutoRefreshSeconds", 5);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("Desktop auto-refresh background task starting. Interval: {Interval}s", intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() => viewModel.RefreshAsync()).Task.Unwrap();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-refresh failed.");
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

        logger.LogInformation("Desktop auto-refresh background task stopping.");
    }
}
