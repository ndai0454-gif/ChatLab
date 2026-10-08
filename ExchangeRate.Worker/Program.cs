using ExchangeRate.Business;
using ExchangeRate.Worker;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ExchangeRateDb")
    ?? throw new InvalidOperationException("Connection string 'ExchangeRateDb' not found.");

builder.Services.AddExchangeRateCore(connectionString);

builder.Services.AddHttpClient("WebPublisher", client =>
{
    var baseUrl = builder.Configuration["Publish:WebBaseUrl"]
        ?? throw new InvalidOperationException("'Publish:WebBaseUrl' not configured.");
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["Publish:ApiKey"]);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
