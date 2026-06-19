using Azure.Monitor.OpenTelemetry.Exporter;
using EpcForwarder.Infrastructure.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

var sqlConnectionString = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("App setting 'SqlConnectionString' is required.");

builder.Services.AddEpcForwarder(new EpcForwarderOptions
{
    SqlConnectionString = sqlConnectionString,
    KeyVaultUri = builder.Configuration["KeyVaultUri"],
});

builder.Build().Run();
