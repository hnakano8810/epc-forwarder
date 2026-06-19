using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Query;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Query;

public class SessionQueryServiceTests
{
    private sealed class Harness
    {
        public InMemorySessionStore Sessions { get; } = new();
        public InMemoryReadingStore Readings { get; } = new();
        public InMemoryProductCatalog Products { get; } = new();
        public FixedClock Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public SessionQueryService Build() => new(Sessions, Readings, Products, Clock);
    }

    private static (Guid id, string unknownEpc) Seed(Harness h, SessionType type)
    {
        var id = Guid.NewGuid();
        h.Sessions.Save(new Session(id, 1, type, "BK-1", h.Clock.UtcNow));
        const string epcA = "302DB42318A0038000001231";
        const string epcB = "302DB42318A0038000009999"; // 同一検索キー(シリアルのみ異なる)
        const string unknown = "302DB42318A0058000007777"; // 異なるアイテム参照→マスタ未登録
        var key = Sgtin96.DeriveSearchKey(epcA);
        h.Products.Add(1, key, "ITEM-AAA");
        h.Readings.Upsert(id, new ReadingEntry(epcA, key, "devA", h.Clock.UtcNow));
        h.Readings.Upsert(id, new ReadingEntry(epcB, key, "devA", h.Clock.UtcNow));
        h.Readings.Upsert(id, new ReadingEntry(unknown, Sgtin96.DeriveSearchKey(unknown), "devA", h.Clock.UtcNow));
        return (id, unknown);
    }

    [Fact]
    public void GetSummary_AggregatesSku_AndCountsUnknown()
    {
        var h = new Harness();
        var (id, _) = Seed(h, SessionType.Shipment);

        var view = h.Build().GetSummary(tenantId: 1, sessionId: id);

        Assert.NotNull(view);
        Assert.Equal("shipment", view!.Type);
        var item = Assert.Single(view.Items);
        Assert.Equal("ITEM-AAA", item.Sku);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(2, view.TotalQuantity);
        Assert.Equal(1, view.UnknownCount);
        Assert.Equal(h.Clock.UtcNow, view.AsOf);
    }

    [Fact]
    public void GetUnknown_ReturnsUnresolvedEpcs()
    {
        var h = new Harness();
        var (id, unknownEpc) = Seed(h, SessionType.Inventory);

        var view = h.Build().GetUnknown(tenantId: 1, sessionId: id);

        Assert.NotNull(view);
        Assert.Equal(1, view!.Count);
        Assert.Equal(unknownEpc, Assert.Single(view.Epcs));
    }

    [Fact]
    public void GetSummary_TenantMismatch_ReturnsNull()
    {
        var h = new Harness();
        var (id, _) = Seed(h, SessionType.Shipment);

        Assert.Null(h.Build().GetSummary(tenantId: 999, sessionId: id));
    }

    [Fact]
    public void GetSummary_UnknownSession_ReturnsNull()
    {
        var h = new Harness();
        Assert.Null(h.Build().GetSummary(tenantId: 1, sessionId: Guid.NewGuid()));
    }

    [Fact]
    public void GetReconciliation_ReturnsExpectedAndReceived()
    {
        var h = new Harness();
        var (id, _) = Seed(h, SessionType.Shipment);
        h.Sessions.Get(id)!.SetExpectedCount(5);

        var view = h.Build().GetReconciliation(tenantId: 1, sessionId: id);

        Assert.NotNull(view);
        Assert.Equal(5, view!.Expected);
        Assert.Equal(3, view.Received);     // 投入3件(解決2+未知1、いずれもユニーク)
        Assert.Equal(2, view.Missing);
        Assert.False(view.IsMatch);
    }

    [Fact]
    public void GetReconciliation_NoExpectedYet_NullMatch()
    {
        var h = new Harness();
        var (id, _) = Seed(h, SessionType.Shipment);

        var view = h.Build().GetReconciliation(tenantId: 1, sessionId: id);

        Assert.NotNull(view);
        Assert.Null(view!.Expected);
        Assert.Null(view.IsMatch);
        Assert.Equal(3, view.Received);
    }

    [Fact]
    public void GetLocationSummary_GroupsByLocation()
    {
        var h = new Harness();
        var id = Guid.NewGuid();
        h.Sessions.Save(new Session(id, 1, SessionType.Inventory, "INV-1", h.Clock.UtcNow));
        const string epcA = "302DB42318A0038000001231";
        const string epcB = "302DB42318A0038000009999"; // 同一SKU・別ロケ
        var key = Sgtin96.DeriveSearchKey(epcA);
        h.Products.Add(1, key, "ITEM-AAA");
        var locA = new ReadLocation("DC", "2F", "A-01");
        var locB = new ReadLocation("DC", "2F", "A-02");
        h.Readings.Upsert(id, new ReadingEntry(epcA, key, "devA", h.Clock.UtcNow, locA));
        h.Readings.Upsert(id, new ReadingEntry(epcB, key, "devA", h.Clock.UtcNow, locB));

        var view = h.Build().GetLocationSummary(tenantId: 1, sessionId: id);

        Assert.NotNull(view);
        Assert.Equal("inventory", view!.Type);
        Assert.Equal(2, view.Locations.Count);
        var a01 = Assert.Single(view.Locations, g => g.Location.L3 == "A-01");
        Assert.Equal(1, a01.TotalQuantity);
        Assert.Equal("ITEM-AAA", Assert.Single(a01.Items).Sku);
    }

    [Fact]
    public void GetReconciliation_TenantMismatch_ReturnsNull()
    {
        var h = new Harness();
        var (id, _) = Seed(h, SessionType.Shipment);
        Assert.Null(h.Build().GetReconciliation(tenantId: 2, sessionId: id));
    }
}
