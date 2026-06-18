// tests/EpcForwarder.Infrastructure.Tests/SqlServerFixture.cs
using EpcForwarder.Infrastructure.Persistence;
using Testcontainers.MsSql;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

/// <summary>テスト全体で1つの SQL Server コンテナを起動し、マイグレーションを適用する。</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        MigrationRunner.Apply(ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;
