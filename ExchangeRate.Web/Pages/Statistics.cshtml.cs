using ExchangeRate.Business.Dtos;
using ExchangeRate.Business.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ExchangeRate.Web.Pages;

public class StatisticsModel(IExchangeRateService service) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateTime? Date { get; set; }

    public List<ExchangeRateStatisticsDto> Statistics { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Statistics = await service.GetStatisticsAsync(Date, ct);
    }
}
