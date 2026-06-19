// tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreLocationTests.cs
using Dapper;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlReadingStoreLocationTests(SqlServerFixture fx)
{
    [Fact]
    public void Upsert_PersistsLocation_And_DerivesTenantFromSession()
    {
        var factory = new SqlConnectionFactory(fx.ConnectionString);
        var sessions = new SqlSessionStore(factory);
        var readings = new SqlReadingStore(factory);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UnixEpoch;
        sessions.Save(new Session(id, fx.NewTenant(), SessionType.Inventory, "INV-LOC", now));

        var loc = new ReadLocation("TOKYO-DC", "2F", "A-01");
        readings.Upsert(id, new ReadingEntry("302DB42318A0038000001231", null, "devA", now, loc));

        var stored = Assert.Single(readings.List(id));
        Assert.Equal(loc, stored.Location);

        using var conn = factory.Create();
        var tenant = conn.ExecuteScalar<int>(
            "SELECT tenant_id FROM dbo.reading WHERE session_id = @id", new { id });
        Assert.NotEqual(0, tenant);
    }
}
