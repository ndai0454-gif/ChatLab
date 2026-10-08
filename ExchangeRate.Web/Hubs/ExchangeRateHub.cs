using Microsoft.AspNetCore.SignalR;

namespace ExchangeRate.Web.Hubs;

/// <summary>Server-to-client only hub. Clients listen for <see cref="RatesUpdatedEvent"/>.</summary>
public class ExchangeRateHub : Hub
{
    public const string Path = "/hubs/exchange-rates";
    public const string RatesUpdatedEvent = "RatesUpdated";
}
