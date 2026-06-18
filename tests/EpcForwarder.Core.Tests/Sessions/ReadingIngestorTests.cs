// tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;
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
}
