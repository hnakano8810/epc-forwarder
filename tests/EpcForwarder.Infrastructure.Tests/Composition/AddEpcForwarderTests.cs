// tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests.Composition;

public class AddEpcForwarderTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // NullDeviceFeedback の ILogger 用
        services.AddEpcForwarder(new EpcForwarderOptions
        {
            SqlConnectionString = "Server=localhost;Database=epcf;Integrated Security=true;TrustServerCertificate=true",
            // KeyVaultUri 未設定 → NullSecretStore
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(typeof(ReadingIngestor))]
    [InlineData(typeof(ShipmentReconciler))]
    [InlineData(typeof(ShipmentDeliverer))]
    [InlineData(typeof(InventoryDeliverer))]
    [InlineData(typeof(ProductRegistrar))]
    [InlineData(typeof(SnapshotPublisher))]
    [InlineData(typeof(IWebhookSender))]
    [InlineData(typeof(ISecretStore))]
    [InlineData(typeof(IClock))]
    [InlineData(typeof(IIdGenerator))]
    [InlineData(typeof(IHostResolver))]
    [InlineData(typeof(IDeviceFeedback))]
    [InlineData(typeof(EpcForwarder.Core.Abstractions.IDestinationCatalog))]
    [InlineData(typeof(IngestionDispatcher))]
    public void Resolves_AllPrimaryServices(Type serviceType)
    {
        using var sp = Build();
        Assert.NotNull(sp.GetRequiredService(serviceType));
    }

    [Fact]
    public void SecretStore_IsCachingDecorator()
    {
        using var sp = Build();
        Assert.IsType<EpcForwarder.Infrastructure.Secrets.CachingSecretStore>(sp.GetRequiredService<ISecretStore>());
    }
}
