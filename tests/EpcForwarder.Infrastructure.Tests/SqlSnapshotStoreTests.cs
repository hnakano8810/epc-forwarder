// tests/EpcForwarder.Infrastructure.Tests/SqlSnapshotStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlSnapshotStoreTests(SqlServerFixture fx)
{
    private SqlSnapshotStore Store() => new(new SqlConnectionFactory(fx.ConnectionString));

    [Fact]
    public void NextVersion_IncrementsPerSession()
    {
        var store = Store();
        var sid = Guid.NewGuid();

        var v1 = store.NextVersion(sid);
        store.Record(new SnapshotRecord(sid, v1, false, Guid.NewGuid(), 3, true));
        var v2 = store.NextVersion(sid);

        Assert.Equal(1, v1);
        Assert.Equal(2, v2);
    }

    [Fact]
    public void Record_Persists_AndIsScopedPerSession()
    {
        var store = Store();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.Record(new SnapshotRecord(a, store.NextVersion(a), true, Guid.NewGuid(), 1, true));

        Assert.Equal(1, store.NextVersion(b)); // 別セッションは1から
    }
}
