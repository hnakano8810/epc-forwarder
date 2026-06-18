// tests/EpcForwarder.Infrastructure.Tests/SqlDestinationStoreTests.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlDestinationStoreTests(SqlServerFixture fx)
{
    private int InsertDestination(int tenantId, string url, bool active, bool hmac, string? hmacRef)
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        return conn.QuerySingle<int>(
            """
            INSERT INTO dbo.destination (tenant_id, name, url, schema_version, hmac_enabled, hmac_secret_ref, is_active)
            OUTPUT INSERTED.destination_id
            VALUES (@tenantId, 'd', @url, '1', @hmac, @hmacRef, @active)
            """,
            new { tenantId, url, hmac, hmacRef, active });
    }

    private void InsertHeader(int destId, string name, string valueRef)
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        conn.Execute(
            "INSERT INTO dbo.destination_header (destination_id, header_name, value_ref) VALUES (@destId, @name, @valueRef)",
            new { destId, name, valueRef });
    }

    [Fact]
    public void GetActiveTargets_BuildsDeliveryTarget_WithHeaders()
    {
        var tenant = fx.NewTenant();
        var id = InsertDestination(tenant, "https://api.example.com/hook", active: true, hmac: true, hmacRef: "hook-hmac");
        InsertHeader(id, "Authorization", "auth-secret");
        InsertHeader(id, "X-API-KEY", "apikey-secret");

        var targets = new SqlDestinationStore(new SqlConnectionFactory(fx.ConnectionString)).GetActiveTargets(tenant);

        var t = Assert.Single(targets);
        Assert.Equal("https://api.example.com/hook", t.Url);
        Assert.Equal("POST", t.Method);
        Assert.Equal("1", t.SchemaVersion);
        Assert.True(t.HmacEnabled);
        Assert.Equal("hook-hmac", t.HmacSecretRef);
        Assert.Equal("auth-secret", t.Headers["Authorization"]);
        Assert.Equal("apikey-secret", t.Headers["X-API-KEY"]);
    }

    [Fact]
    public void GetActiveTargets_ExcludesInactive_AndOtherTenants()
    {
        var tenant = fx.NewTenant();
        var other = fx.NewTenant();
        InsertDestination(tenant, "https://active.example.com", active: true, hmac: false, hmacRef: null);
        InsertDestination(tenant, "https://inactive.example.com", active: false, hmac: false, hmacRef: null);
        InsertDestination(other, "https://other.example.com", active: true, hmac: false, hmacRef: null);

        var targets = new SqlDestinationStore(new SqlConnectionFactory(fx.ConnectionString)).GetActiveTargets(tenant);

        var t = Assert.Single(targets);
        Assert.Equal("https://active.example.com", t.Url);
        Assert.Empty(t.Headers);
    }

    [Fact]
    public void GetActiveTargets_NoDestinations_ReturnsEmpty()
    {
        var tenant = fx.NewTenant();
        Assert.Empty(new SqlDestinationStore(new SqlConnectionFactory(fx.ConnectionString)).GetActiveTargets(tenant));
    }
}
