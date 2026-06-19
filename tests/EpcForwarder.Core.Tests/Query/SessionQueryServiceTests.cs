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

    private static (Guid id, string keyEpcA, string keyEpcB, string unknownEpc) Seed(Harness h, SessionType type)
    {
        var id = Guid.NewGuid();
        h.Sessions.Save(new Session(id, 1, type, "BK-1", h.Clock.UtcNow));
        const string epcA = "302DB42318A0038000001231";
        const string epcB = "302DB42318A0038000009999"; // 同一検索キー(シリアルのみ異なる)
        const string unknown = "302DB42318A0058000007777"; // 異なるアイテム参照→マスタ未登録
        var key = Sgtin96.DeriveSearchKey(epcA);
        h.Products.Add(1, key, "ITEM-AAA");
        h.Readings.Upsert(id, new EpcForwarder.Core.Abstractions.ReadingEntry(epcA, key, "devA", h.Clock.UtcNow));
        h.Readings.Upsert(id, new EpcForwarder.Core.Abstractions.ReadingEntry(epcB, key, "devA", h.Clock.UtcNow));
        h.Readings.Upsert(id, new EpcForwarder.Core.Abstractions.ReadingEntry(unknown, Sgtin96.DeriveSearchKey(unknown), "devA", h.Clock.UtcNow));
        return (id, epcA, epcB, unknown);
    }

    [Fact]
    public void GetSummary_AggregatesSku_AndCountsUnknown()
    {
        var h = new Harness();
        var (id, _, _, _) = Seed(h, SessionType.Shipment);

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
        var (id, _, _, unknownEpc) = Seed(h, SessionType.Inventory);

        var view = h.Build().GetUnknown(tenantId: 1, sessionId: id);

        Assert.NotNull(view);
        Assert.Equal(1, view!.Count);
        Assert.Equal(unknownEpc, Assert.Single(view.Epcs));
    }

    [Fact]
    public void GetSummary_TenantMismatch_ReturnsNull()
    {
        var h = new Harness();
        var (id, _, _, _) = Seed(h, SessionType.Shipment);

        Assert.Null(h.Build().GetSummary(tenantId: 999, sessionId: id));
    }

    [Fact]
    public void GetSummary_UnknownSession_ReturnsNull()
    {
        var h = new Harness();
        Assert.Null(h.Build().GetSummary(tenantId: 1, sessionId: Guid.NewGuid()));
    }
}
