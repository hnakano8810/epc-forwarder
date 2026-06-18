// tests/EpcForwarder.Core.Tests/Delivery/SnapshotPublisherTests.cs
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class SnapshotPublisherTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task Publish_SetsIsFinal_OnEnvelopeHeaderAndSnapshot(bool isFinal, string headerValue)
    {
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snapshots = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        var publisher = new SnapshotPublisher(readings, products, snapshots, sender, secrets, new PayloadBuilder(), clock, ids);

        var session = new Session(Guid.NewGuid(), 1, SessionType.Inventory, "CAMP-1", clock.UtcNow);

        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null,
            new Dictionary<string, string>());

        var result = await publisher.PublishAsync(session, "inventory", isFinal, target);

        Assert.True(result.Success);
        Assert.Equal(headerValue, sender.Last!.Headers["X-EPCF-Is-Final"]);
        Assert.Contains($"\"is_final\":{headerValue}", sender.Last!.Body);
        var snap = Assert.Single(snapshots.Records);
        Assert.Equal(isFinal, snap.IsFinal);
        Assert.Equal(1, snap.Version);
    }
}
