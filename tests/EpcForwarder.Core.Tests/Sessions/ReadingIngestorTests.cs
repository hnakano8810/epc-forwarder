// tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs
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
}
