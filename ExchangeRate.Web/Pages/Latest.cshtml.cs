using ExchangeRate.Business.Dtos;
using ExchangeRate.Business.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ExchangeRate.Web.Pages;

public class LatestModel(IExchangeRateService service) : PageModel
{
    public List<ExchangeRateDto> Rates { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rates = await service.GetLatestAsync(ct);
    }
}
