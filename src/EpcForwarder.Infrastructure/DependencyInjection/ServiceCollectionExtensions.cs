// src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Delivery;
using EpcForwarder.Infrastructure.Messaging;
using EpcForwarder.Infrastructure.Net;
using EpcForwarder.Infrastructure.Persistence;
using EpcForwarder.Infrastructure.Runtime;
using EpcForwarder.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace EpcForwarder.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>SQL永続化＋アダプタ＋Coreサービスを1つのオブジェクトグラフとして登録する。</summary>
    public static IServiceCollection AddEpcForwarder(this IServiceCollection services, EpcForwarderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SqlConnectionString);

        // 永続化(4ポート)
        services.AddSqlPersistence(options.SqlConnectionString);

        // ランタイム・アダプタ
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<IDeviceFeedback, NullDeviceFeedback>();
        services.AddSingleton(new HttpClient()); // TODO: replace with IHttpClientFactory in the Functions host (③b)
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();

        // シークレット: (Key Vault or Null) を TTL キャッシュで包む
        services.AddSingleton<ISecretStore>(sp =>
        {
            ISecretStore inner = options.KeyVaultUri is { Length: > 0 } uri
                ? new KeyVaultSecretStore(new SecretClient(new Uri(uri), new DefaultAzureCredential()))
                : new NullSecretStore();
            return new CachingSecretStore(inner, sp.GetRequiredService<IClock>(), options.SecretCacheTtl);
        });

        // Core サービス
        services.AddSingleton<PayloadBuilder>();
        services.AddSingleton<SnapshotPublisher>();
        services.AddSingleton<ReadingIngestor>();
        services.AddSingleton<ShipmentReconciler>();
        services.AddSingleton<ShipmentDeliverer>();
        services.AddSingleton<InventoryDeliverer>();
        services.AddSingleton<ProductRegistrar>();

        return services;
    }
}
