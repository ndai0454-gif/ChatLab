using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatLab.Hub;

[Authorize]
public sealed class ExchangeRatesHub : Microsoft.AspNetCore.SignalR.Hub
{
    public const string Path = "/hubs/exchange-rates";
    public const string RatesUpdatedEvent = "RatesUpdated";
}
