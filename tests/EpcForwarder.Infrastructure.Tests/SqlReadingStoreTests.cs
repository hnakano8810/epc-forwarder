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

    [Fact]
    public void UpsertBatch_InsertsAllRows_InOneCall()
    {
        var (store, sid) = StoreWithSession();
        store.UpsertBatch(sid,
        [
            new ReadingEntry("302DB42318A0038000001231", null, "devA", DateTimeOffset.UnixEpoch),
            new ReadingEntry("302DB42318A0038000001232", null, "devA", DateTimeOffset.UnixEpoch),
            new ReadingEntry("302DB42318A0038000001233", null, "devA", DateTimeOffset.UnixEpoch),
        ]);

        Assert.Equal(3, store.CountUnique(sid));
    }

    [Fact]
    public void UpsertBatch_SameEpcDifferentLocation_LastWriteWins()
    {
        var (store, sid) = StoreWithSession();
        var t0 = DateTimeOffset.UnixEpoch;

        store.UpsertBatch(sid, [new ReadingEntry("302DB42318A0038000001231", null, "devA", t0, new ReadLocation("DC", "1F", "A-01"))]);
        // 別ロケ・新しい時刻で再投入 → 後勝ちで location 上書き、件数は不変。
        store.UpsertBatch(sid, [new ReadingEntry("302DB42318A0038000001231", null, "devB", t0.AddSeconds(5), new ReadLocation("DC", "2F", "B-02"))]);

        Assert.Equal(1, store.CountUnique(sid));
        var row = store.List(sid).Single();
        Assert.Equal(new ReadLocation("DC", "2F", "B-02"), row.Location);
        Assert.Equal("devB", row.DeviceId);
    }

    [Fact]
    public void UpsertBatch_ResentBatch_IsIdempotent()
    {
        var (store, sid) = StoreWithSession();
        ReadingEntry[] batch =
        [
            new("302DB42318A0038000001231", null, "devA", DateTimeOffset.UnixEpoch),
            new("302DB42318A0038000001232", null, "devA", DateTimeOffset.UnixEpoch),
        ];

        store.UpsertBatch(sid, batch);
        store.UpsertBatch(sid, batch);   // at-least-once 再送

        Assert.Equal(2, store.CountUnique(sid));
    }
}
