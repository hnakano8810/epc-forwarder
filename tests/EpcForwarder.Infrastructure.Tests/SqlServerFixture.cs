// tests/EpcForwarder.Infrastructure.Tests/SqlServerFixture.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

/// <summary>テスト全体で1つの SQL Server コンテナを起動し、マイグレーションを適用する。</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
#pragma warning disable CS0618 // MsSqlBuilder() parameterless ctor is obsolete in 4.12; default image is acceptable for tests
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();
#pragma warning restore CS0618

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        MigrationRunner.Apply(ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public int NewTenant()
    {
        using var conn = new SqlConnection(ConnectionString);
        return conn.QuerySingle<int>(
            "INSERT INTO dbo.tenant(code,name) OUTPUT INSERTED.tenant_id VALUES(@c,'t')",
            new { c = Guid.NewGuid().ToString("N") });
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;
