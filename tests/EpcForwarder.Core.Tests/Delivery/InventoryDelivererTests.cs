// tests/EpcForwarder.Core.Tests/Delivery/InventoryDelivererTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class InventoryDelivererTests
{
    private static (InventoryDeliverer Sut, Guid Id, InMemorySessionStore Sessions, InMemorySnapshotStore Snaps, CapturingWebhookSender Sender)
        Build()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snaps = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        var publisher = new SnapshotPublisher(readings, products, snaps, sender, secrets, new PayloadBuilder(), clock, ids);

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "CAMP-1", clock.UtcNow));
        return (new InventoryDeliverer(sessions, publisher, clock), id, sessions, snaps, sender);
    }

    private static DeliveryTarget Target() =>
        new("https://api.example.com/hook", "POST", "1", false, null, new Dictionary<string, string>());

    [Fact]
    public async Task Provisional_KeepsSessionOpen_AndMarksNotFinal()
    {
        var (sut, id, sessions, snaps, sender) = Build();

        var result = await sut.SendProvisionalAsync(id, Target());

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Open, sessions.Get(id)!.Status); // open のまま
        Assert.Equal("false", sender.Last!.Headers["X-EPCF-Is-Final"]);
        Assert.False(snaps.Records.Single().IsFinal);
    }

    [Fact]
    public async Task Provisional_SendFailure_StaysOpen_RecordsFailedSnapshot()
    {
        var (sut, id, sessions, snaps, sender) = Build();
        sender.Next = new WebhookResult(false, 500);

        var result = await sut.SendProvisionalAsync(id, Target());

        Assert.False(result.Success);
        Assert.Equal(SessionStatus.Open, sessions.Get(id)!.Status); // 失敗でも仮確定はopen維持
        var snap = Assert.Single(snaps.Records);
        Assert.False(snap.Success);
        Assert.False(snap.IsFinal);
    }

    [Fact]
    public async Task Provisional_CanRepeat_VersionsIncrement_SessionStaysOpen()
    {
        var (sut, id, sessions, snaps, _) = Build();

        await sut.SendProvisionalAsync(id, Target());
        await sut.SendProvisionalAsync(id, Target());

        Assert.Equal(SessionStatus.Open, sessions.Get(id)!.Status);
        Assert.Equal(new[] { 1, 2 }, snaps.Records.Select(r => r.Version).ToArray());
        Assert.All(snaps.Records, r => Assert.False(r.IsFinal));
    }

    [Fact]
    public async Task Finalize_SendsFinal_AndForwards()
    {
        var (sut, id, sessions, snaps, sender) = Build();

        var result = await sut.FinalizeAndDeliverAsync(id, Target());

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.Equal("true", sender.Last!.Headers["X-EPCF-Is-Final"]);
        Assert.True(snaps.Records.Single().IsFinal);
    }

    [Fact]
    public async Task Provisional_AfterForwarded_Throws()
    {
        var (sut, id, _, _, _) = Build();
        await sut.FinalizeAndDeliverAsync(id, Target());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SendProvisionalAsync(id, Target()));
    }

    [Fact]
    public async Task ProvisionalThenFinalize_ProducesTwoSnapshots_FinalLast()
    {
        var (sut, id, sessions, snaps, sender) = Build();

        await sut.SendProvisionalAsync(id, Target());
        var result = await sut.FinalizeAndDeliverAsync(id, Target());

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.Equal(2, sender.Requests.Count);
        Assert.Equal(new[] { false, true }, snaps.Records.Select(r => r.IsFinal).ToArray());
        Assert.Equal(new[] { 1, 2 }, snaps.Records.Select(r => r.Version).ToArray());
    }
}
