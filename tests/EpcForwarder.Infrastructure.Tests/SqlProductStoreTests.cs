// tests/EpcForwarder.Infrastructure.Tests/SqlProductStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlProductStoreTests(SqlServerFixture fx)
{
    [Fact]
    public void Upsert_ThenResolveSku()
    {
        var store = new SqlProductStore(new SqlConnectionFactory(fx.ConnectionString));
        var tenant = fx.NewTenant();
        store.Upsert(new ProductRecord(tenant, "302DB42318A0038000000000", "ITEM-AAA", "ST-100", "BLK", "M", null));

        Assert.Equal("ITEM-AAA", store.ResolveSku(tenant, "302DB42318A0038000000000"));
        Assert.Null(store.ResolveSku(tenant, "FFFFFFFFFFFFFFFFFFFFFFFF"));
    }

    [Fact]
    public void Upsert_SameKey_Overwrites()
    {
        var store = new SqlProductStore(new SqlConnectionFactory(fx.ConnectionString));
        var tenant = fx.NewTenant();
        store.Upsert(new ProductRecord(tenant, "AABB", "OLD", null, null, null, null));
        store.Upsert(new ProductRecord(tenant, "AABB", "NEW", null, null, null, null));

        Assert.Equal("NEW", store.ResolveSku(tenant, "AABB"));
    }
}
