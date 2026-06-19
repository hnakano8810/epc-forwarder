using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Inventory;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Inventory;

public class InventoryDispatcherTests
{
    private sealed class Harness
    {
        public InMemorySessionStore Sessions { get; } = new();
        public InMemoryReadingStore Readings { get; } = new();
        public InMemoryProductCatalog Products { get; } = new();
        public InMemorySnapshotStore Snapshots { get; } = new();
        public FakeSecretStore Secrets { get; } = new();
        public CapturingWebhookSender Sender { get; } = new();
        public FakeDestinationCatalog Destinations { get; } = new();
        public FixedClock Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public SequentialIdGenerator Ids { get; } = new();

        public InventoryDispatcher Build()
        {
            var publisher = new SnapshotPublisher(Readings, Products, Snapshots, Sender, Secrets, new PayloadBuilder(), Clock, Ids);
            var deliverer = new InventoryDeliverer(Sessions, publisher, Clock);
            return new InventoryDispatcher(Sessions, deliverer, Destinations);
        }
    }

    private static DeliveryTarget Target(string url) =>
        new(url, "POST", "1", HmacEnabled: false, HmacSecretRef: null, Headers: new Dictionary<string, string>());

    private Guid OpenInventory(Harness h)
    {
        var id = Guid.NewGuid();
        h.Sessions.Save(new Session(id, 1, SessionType.Inventory, "INV-1", h.Clock.UtcNow));
        return id;
    }

    [Fact]
    public async Task SendProvisional_Match_DeliversAndKeepsOpen()
    {
        var h = new Harness();
        h.Destinations.Add(1, Target("https://example.test/hook"));
        var d = h.Build();
        var id = OpenInventory(h);

        var outcome = await d.SendProvisionalAsync(tenantId: 1, sessionId: id);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Delivered);
        Assert.Equal(1, h.Sender.SendCount);
        Assert.Equal(SessionStatus.Open, h.Sessions.Get(id)!.Status);
    }

    [Fact]
    public async Task Finalize_Match_DeliversAndForwards()
    {
        var h = new Harness();
        h.Destinations.Add(1, Target("https://example.test/hook"));
        var d = h.Build();
        var id = OpenInventory(h);

        var outcome = await d.FinalizeAndDeliverAsync(tenantId: 1, sessionId: id);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Delivered);
        Assert.Equal(SessionStatus.Forwarded, h.Sessions.Get(id)!.Status);
    }

    [Fact]
    public async Task SendProvisional_NoActiveTarget_NotDelivered()
    {
        var h = new Harness();
        var d = h.Build();
        var id = OpenInventory(h);

        var outcome = await d.SendProvisionalAsync(tenantId: 1, sessionId: id);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Delivered);
        Assert.Equal(0, h.Sender.SendCount);
    }

    [Fact]
    public async Task SendProvisional_TenantMismatch_ReturnsNull()
    {
        var h = new Harness();
        h.Destinations.Add(1, Target("https://example.test/hook"));
        var d = h.Build();
        var id = OpenInventory(h);

        Assert.Null(await d.SendProvisionalAsync(tenantId: 999, sessionId: id));
    }

    [Fact]
    public async Task Finalize_UnknownSession_ReturnsNull()
    {
        var h = new Harness();
        var d = h.Build();
        Assert.Null(await d.FinalizeAndDeliverAsync(tenantId: 1, sessionId: Guid.NewGuid()));
    }
}
