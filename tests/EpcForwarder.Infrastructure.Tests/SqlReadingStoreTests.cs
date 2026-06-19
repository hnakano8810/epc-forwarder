// tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlReadingStoreTests(SqlServerFixture fx)
{
    private SqlConnectionFactory Factory() => new(fx.ConnectionString);

    /// <summary>セッションを事前作成してから ReadingStore を返す。tenant_id サブクエリのため必須。</summary>
    private (SqlReadingStore store, Guid sessionId) StoreWithSession()
    {
        var factory = Factory();
        var sessionId = Guid.NewGuid();
        new SqlSessionStore(factory).Save(new Session(sessionId, fx.NewTenant(), SessionType.Inventory, "TEST", DateTimeOffset.UnixEpoch));
        return (new SqlReadingStore(factory), sessionId);
    }

    [Fact]
    public void Upsert_SameEpc_LastWriteWins()
    {
        var (store, sid) = StoreWithSession();
        store.Upsert(sid, new ReadingEntry("302DB42318A0038000001231", "302DB42318A0038000000000", "devA", DateTimeOffset.UnixEpoch));
        store.Upsert(sid, new ReadingEntry("302DB42318A0038000001231", "302DB42318A0038000000000", "devB", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.Equal(1, store.CountUnique(sid));
        Assert.Equal("devB", store.List(sid).Single().DeviceId);
    }

    [Fact]
    public void List_ReturnsAllUniqueEpcs_ForSession()
    {
        var (store, sid) = StoreWithSession();
        store.Upsert(sid, new ReadingEntry("AA01", "AA00", "d", DateTimeOffset.UnixEpoch));
        store.Upsert(sid, new ReadingEntry("AA02", "AA00", "d", DateTimeOffset.UnixEpoch));

        Assert.Equal(2, store.CountUnique(sid));
        Assert.Equal(new[] { "AA01", "AA02" }, store.List(sid).Select(r => r.Epc).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Upsert_NullSearchKey_RoundTrips()
    {
        var (store, sid) = StoreWithSession();
        store.Upsert(sid, new ReadingEntry("BB01", null, "d", DateTimeOffset.UnixEpoch));
        Assert.Null(store.List(sid).Single().SearchKey);
    }
}
