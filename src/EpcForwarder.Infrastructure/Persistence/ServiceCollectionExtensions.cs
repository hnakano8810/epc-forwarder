// src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs
using EpcForwarder.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EpcForwarder.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>Azure SQL 永続化(4ポート)を登録する。</summary>
    public static IServiceCollection AddSqlPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(new SqlConnectionFactory(connectionString));
        services.AddSingleton<ISessionStore, SqlSessionStore>();
        services.AddSingleton<IReadingStore, SqlReadingStore>();
        services.AddSingleton<ISnapshotStore, SqlSnapshotStore>();
        services.AddSingleton<SqlProductStore>();
        services.AddSingleton<IProductCatalog>(sp => sp.GetRequiredService<SqlProductStore>());
        services.AddSingleton<IProductWriteStore>(sp => sp.GetRequiredService<SqlProductStore>());
        services.AddSingleton<IDestinationCatalog, SqlDestinationStore>();
        services.AddSingleton<ITenantLookup, SqlTenantLookup>();
        return services;
    }
}
