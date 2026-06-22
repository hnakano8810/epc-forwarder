using EpcForwarder.Functions.Auth;
using EpcForwarder.Infrastructure.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights(.NET isolated 標準連携)。
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

var sqlConnectionString = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("App setting 'SqlConnectionString' is required.");

builder.Services.AddEpcForwarder(new EpcForwarderOptions
{
    SqlConnectionString = sqlConnectionString,
    KeyVaultUri = builder.Configuration["KeyVaultUri"],
});

// External ID トークン検証(HTTP API 用)。
var authOptions = new AuthOptions
{
    Issuer = builder.Configuration["Auth:Issuer"] ?? "",
    Audience = builder.Configuration["Auth:Audience"] ?? "",
    TenantClaim = builder.Configuration["Auth:TenantClaim"] ?? "",
    MetadataAddress = builder.Configuration["Auth:MetadataAddress"] ?? "",
};
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton<ITokenValidationParametersProvider>(_ => new OpenIdTokenValidationParametersProvider(authOptions));
builder.Services.AddSingleton<JwtBearerValidator>();

builder.UseMiddleware<AuthenticationMiddleware>();

builder.Build().Run();
