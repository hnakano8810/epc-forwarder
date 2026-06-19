using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Ingestion;

public class IngestionDispatcherTests
{
    private sealed class Harness
    {
        public InMemorySessionStore Sessions { get; } = new();
        public InMemoryReadingStore Readings { get; } = new();
        public InMemoryProductCatalog Products { get; } = new();
        public InMemorySnapshotStore Snapshots { get; } = new();
        public FakeSecretStore Secrets { get; } = new();
        public CapturingWebhookSender Sender { get; } = new();
        public CapturingDeviceFeedback Feedback { get; } = new();
        public FakeDestinationCatalog Destinations { get; } = new();
        public FixedClock Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public SequentialIdGenerator Ids { get; } = new();

        public IngestionDispatcher Build()
        {
            var publisher = new SnapshotPublisher(Readings, Products, Snapshots, Sender, Secrets, new PayloadBuilder(), Clock, Ids);
            var ingestor = new ReadingIngestor(Sessions, Readings, Clock);
            var reconciler = new ShipmentReconciler(Sessions, Readings, Feedback, Clock);
            var deliverer = new ShipmentDeliverer(Sessions, publisher, Clock);
            return new IngestionDispatcher(Sessions, ingestor, reconciler, deliverer, Destinations, Clock);
        }
    }

    private static ReadCommand Read(Guid id, string epc, bool resolveSku = true) =>
        new(1, id, "DN-1", SessionType.Shipment, resolveSku, epc, "devA", null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void IngestRead_UnknownSession_LazyCreatesSessionAndStoresReading()
    {
        var h = new Harness();
        var d = h.Build();
        var id = Guid.NewGuid();

        d.IngestRead(Read(id, "302DB42318A0038000001231"));

        var session = h.Sessions.Get(id);
        Assert.NotNull(session);
        Assert.Equal(1, session!.TenantId);
        Assert.Equal(SessionType.Shipment, session.Type);
        Assert.Equal("DN-1", session.BusinessKey);
        Assert.Single(h.Readings.List(id));
    }

    [Fact]
    public void IngestRead_ExistingSession_DoesNotOverwriteMetadata()
    {
        var h = new Harness();
        var d = h.Build();
        var id = Guid.NewGuid();
        h.Sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-EXISTING", h.Clock.UtcNow));

        d.IngestRead(Read(id, "302DB42318A0038000001231"));

        Assert.Equal("DN-EXISTING", h.Sessions.Get(id)!.BusinessKey);
    }
}
