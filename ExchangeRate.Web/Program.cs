using ExchangeRate.Business;
using ExchangeRate.Business.Dtos;
using ExchangeRate.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ExchangeRateDb")
    ?? throw new InvalidOperationException("Connection string 'ExchangeRateDb' not found.");

builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddExchangeRateCore(connectionString);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapHub<ExchangeRateHub>(ExchangeRateHub.Path);

// Producer (ExchangeRate.Worker) posts freshly saved rates here; the web app fans them out to browsers.
app.MapPost("/api/rates/publish", async (
    List<ExchangeRateDto> rates,
    HttpRequest request,
    IConfiguration configuration,
    IHubContext<ExchangeRateHub> hub) =>
{
    var expectedKey = configuration["Publish:ApiKey"];
    if (string.IsNullOrEmpty(expectedKey) || request.Headers["X-Api-Key"] != expectedKey)
    {
        return Results.Unauthorized();
    }

    await hub.Clients.All.SendAsync(ExchangeRateHub.RatesUpdatedEvent, rates);
    return Results.NoContent();
});

app.Run();
