// tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class ReadingIngestorTests
{
    [Fact]
    public void Ingest_ResolvesSearchKey_AndStores()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var sut = new ReadingIngestor(sessions, readings, clock);

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));

        sut.Ingest(id, "302DB42318A0038000001231", "devA", clock.UtcNow, resolveSku: true);

        var entry = Assert.Single(readings.List(id));
        Assert.Equal(Sgtin96.DeriveSearchKey("302DB42318A0038000001231"), entry.SearchKey);
    }

    [Fact]
    public void Ingest_UnknownSession_Throws()
    {
        var sut = new ReadingIngestor(new InMemorySessionStore(), new InMemoryReadingStore(),
            new FixedClock(DateTimeOffset.UnixEpoch));

        Assert.Throws<InvalidOperationException>(() =>
            sut.Ingest(Guid.NewGuid(), "302DB42318A0038000001231", "devA", DateTimeOffset.UnixEpoch, true));
    }

    [Fact]
    public void Ingest_RawMode_LeavesSearchKeyNull()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var sut = new ReadingIngestor(sessions, readings, clock);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));

        sut.Ingest(id, "FREEFORM-EPC", "devA", clock.UtcNow, resolveSku: false);

        Assert.Null(readings.List(id).Single().SearchKey);
    }

    [Fact]
    public void Ingest_WithLocation_StoresLocationOnEntry()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "INV-1", clock.UtcNow));

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var loc = new ReadLocation("TOKYO-DC", "2F", "A-01");

        ingestor.Ingest(id, "302DB42318A0038000001231", "devA", clock.UtcNow, resolveSku: false, location: loc);

        var stored = Assert.Single(readings.List(id));
        Assert.Equal(loc, stored.Location);
    }

    [Fact]
    public void IngestBatch_DedupesSameEpc_LastWriteByReadAt_AndUpsertsOnce()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var sut = new ReadingIngestor(sessions, readings, clock);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "INV-1", clock.UtcNow));

        var t0 = DateTimeOffset.UnixEpoch;
        var locOld = new ReadLocation("DC", "1F", "A-01");
        var locNew = new ReadLocation("DC", "2F", "B-02");
        // 同一EPCを別ロケ・別時刻で2回 + 別EPC1回。後勝ち(最大 read_at)で 2件に収束。
        sut.IngestBatch(id,
            [
                new ReadEntry("302DB42318A0038000001231", t0, locOld),
                new ReadEntry("302DB42318A0038000001231", t0.AddSeconds(5), locNew),
                new ReadEntry("302DB42318A0038000001232", t0, null),
            ],
            deviceId: "devA", resolveSku: true);

        var list = readings.List(id);
        Assert.Equal(2, list.Count);
        var dup = list.Single(r => r.Epc == "302DB42318A0038000001231");
        Assert.Equal(locNew, dup.Location);                 // 後勝ち: 最新ロケ
        Assert.Equal(t0.AddSeconds(5), dup.ReadAt);          // 後勝ち: 最新時刻
        Assert.Equal(Sgtin96.DeriveSearchKey("302DB42318A0038000001231"), dup.SearchKey);
        Assert.Equal(1, readings.UpsertBatchCalls);          // 一括は1回のみ
    }

    [Fact]
    public void IngestBatch_RawMode_LeavesSearchKeyNull()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var sut = new ReadingIngestor(sessions, readings, clock);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "INV-1", clock.UtcNow));

        sut.IngestBatch(id,
            [new ReadEntry("302DB42318A0038000001231", DateTimeOffset.UnixEpoch, null)],
            deviceId: "devA", resolveSku: false);

        Assert.Null(readings.List(id).Single().SearchKey);
    }

    [Fact]
    public void IngestBatch_Empty_NoUpsert_StillTouchesSession()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var sut = new ReadingIngestor(sessions, readings, clock);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "INV-1", clock.UtcNow));

        sut.IngestBatch(id, [], deviceId: "devA", resolveSku: true);

        Assert.Empty(readings.List(id));
        Assert.Equal(0, readings.UpsertBatchCalls);
    }

    [Fact]
    public void IngestBatch_UnknownSession_Throws()
    {
        var sut = new ReadingIngestor(new InMemorySessionStore(), new InMemoryReadingStore(),
            new FixedClock(DateTimeOffset.UnixEpoch));

        Assert.Throws<InvalidOperationException>(() =>
            sut.IngestBatch(Guid.NewGuid(),
                [new ReadEntry("302DB42318A0038000001231", DateTimeOffset.UnixEpoch, null)],
                deviceId: "devA", resolveSku: true));
    }
}
