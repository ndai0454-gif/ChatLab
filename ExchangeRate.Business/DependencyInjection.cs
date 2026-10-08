using ExchangeRate.Business.Services;
using ExchangeRate.Data;
using ExchangeRate.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExchangeRate.Business;

public static class DependencyInjection
{
    /// <summary>Registers DbContext, repositories and services (Data + Business layers).</summary>
    public static IServiceCollection AddExchangeRateCore(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<ExchangeRateDbContext>(o => o.UseSqlServer(connectionString));
        services.AddScoped<IExchangeRateRepository, ExchangeRateRepository>();
        services.AddScoped<IExchangeRateService, ExchangeRateService>();
        return services;
    }
}
