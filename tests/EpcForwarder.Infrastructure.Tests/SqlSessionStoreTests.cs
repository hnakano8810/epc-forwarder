// tests/EpcForwarder.Infrastructure.Tests/SqlSessionStoreTests.cs
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlSessionStoreTests(SqlServerFixture fx)
{
    [Fact]
    public void Get_Unknown_ReturnsNull()
    {
        var store = new SqlSessionStore(new SqlConnectionFactory(fx.ConnectionString));
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Save_Insert_Then_Get_RoundTrips()
    {
        var store = new SqlSessionStore(new SqlConnectionFactory(fx.ConnectionString));
        var id = Guid.NewGuid();
        var s = new Session(id, fx.NewTenant(), SessionType.Inventory, "CAMP-1", DateTimeOffset.UnixEpoch);

        store.Save(s);
        var loaded = store.Get(id)!;

        Assert.Equal(SessionType.Inventory, loaded.Type);
        Assert.Equal("CAMP-1", loaded.BusinessKey);
        Assert.Equal(SessionStatus.Open, loaded.Status);
    }

    [Fact]
    public void Save_Update_PersistsStatusTransition()
    {
        var store = new SqlSessionStore(new SqlConnectionFactory(fx.ConnectionString));
        var id = Guid.NewGuid();
        var s = new Session(id, fx.NewTenant(), SessionType.Shipment, "DN-1", DateTimeOffset.UnixEpoch);
        store.Save(s);

        s.Finalize(DateTimeOffset.UnixEpoch.AddMinutes(1));
        s.MarkForwarded(DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.Save(s); // upsert

        var loaded = store.Get(id)!;
        Assert.Equal(SessionStatus.Forwarded, loaded.Status);
        Assert.NotNull(loaded.FinalizedAt);
        Assert.NotNull(loaded.ForwardedAt);
    }
}
