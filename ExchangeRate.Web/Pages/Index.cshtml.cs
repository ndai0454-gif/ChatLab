using ExchangeRate.Business.Dtos;
using ExchangeRate.Business.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ExchangeRate.Web.Pages;

public class IndexModel(IExchangeRateService service) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateTime? Date { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Currency { get; set; }

    public List<ExchangeRateDto> Rates { get; private set; } = new();
    public List<string> AvailableCurrencies { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Rates = await service.SearchAsync(Date, Currency, ct);
        AvailableCurrencies = await service.GetCurrenciesAsync(ct);
    }
}
