using EpcForwarder.Infrastructure.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights(.NET isolated 標準連携)。APPLICATIONINSIGHTS_CONNECTION_STRING があれば自動エクスポート。
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

var sqlConnectionString = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("App setting 'SqlConnectionString' is required.");

builder.Services.AddEpcForwarder(new EpcForwarderOptions
{
    SqlConnectionString = sqlConnectionString,
    KeyVaultUri = builder.Configuration["KeyVaultUri"],
});

builder.Build().Run();
