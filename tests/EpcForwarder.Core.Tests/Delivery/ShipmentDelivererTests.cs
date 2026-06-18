// tests/EpcForwarder.Core.Tests/Delivery/ShipmentDelivererTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class ShipmentDelivererTests
{
    private const string EpcA = "302DB42318A0038000001231";
    private const string EpcB = "302DB42318A0038000009999"; // 同一商品(同一検索キー)別シリアル

    [Fact]
    public async Task FinalizeAndDeliver_Aggregates_Signs_Records_AndForwards()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snapshots = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        secrets.Add("hook-hmac", "topsecret");

        var key = Sgtin96.DeriveSearchKey(EpcA); // EpcA/EpcB は同一検索キー
        products.Add(1, key, "ITEM-AAA");

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));
        readings.Upsert(id, new ReadingEntry(EpcA, key, "devA", clock.UtcNow));
        readings.Upsert(id, new ReadingEntry(EpcB, key, "devA", clock.UtcNow));

        var sut = new ShipmentDeliverer(sessions, readings, products, snapshots, sender, secrets,
            new PayloadBuilder(), clock, ids);

        var target = new DeliveryTarget(
            Url: "https://api.example.com/hook",
            Method: "POST",
            SchemaVersion: "1",
            HmacEnabled: true,
            HmacSecretRef: "hook-hmac",
            Headers: new Dictionary<string, string>());

        var result = await sut.FinalizeAndDeliverAsync(id, target);

        Assert.True(result.Success);
        // セッションは forwarded
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        // スナップショット記録
        var snap = Assert.Single(snapshots.Records);
        Assert.True(snap.IsFinal);
        Assert.Equal(1, snap.Version);
        // 送信内容
        var req = sender.Last!;
        Assert.Contains("\"sku\":\"ITEM-AAA\"", req.Body);
        Assert.Contains("\"quantity\":2", req.Body); // EpcA+EpcB が同一SKUに集約
        Assert.True(req.Headers.ContainsKey("Idempotency-Key"));
        Assert.Equal("true", req.Headers["X-EPCF-Is-Final"]);
        var expectedSig = HmacSigner.Sign("topsecret", "1970-01-01T00:00:00Z", req.Body);
        Assert.Equal(expectedSig, req.Headers["X-EPCF-Signature"]);
    }

    [Fact]
    public async Task FinalizeAndDeliver_SendFailure_NotForwarded_RecordsFailedSnapshot()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snapshots = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender { Next = new WebhookResult(false, 500) };
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();

        var key = Sgtin96.DeriveSearchKey(EpcA);
        products.Add(1, key, "ITEM-AAA");

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));
        readings.Upsert(id, new ReadingEntry(EpcA, key, "devA", clock.UtcNow));

        var sut = new ShipmentDeliverer(sessions, readings, products, snapshots, sender, secrets,
            new PayloadBuilder(), clock, ids);

        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null,
            new Dictionary<string, string>());

        var result = await sut.FinalizeAndDeliverAsync(id, target);

        Assert.False(result.Success);
        // 送信失敗 → forwarded にならず finalized 止まり（リトライ可能）
        Assert.Equal(SessionStatus.Finalized, sessions.Get(id)!.Status);
        var snap = Assert.Single(snapshots.Records);
        Assert.False(snap.Success);
    }

    [Fact]
    public async Task FinalizeAndDeliver_UnknownTags_GoToSeparateLane()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog(); // 何も登録しない → 全部未知
        var snapshots = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();

        var key = Sgtin96.DeriveSearchKey(EpcA);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));
        readings.Upsert(id, new ReadingEntry(EpcA, key, "devA", clock.UtcNow));

        var sut = new ShipmentDeliverer(sessions, readings, products, snapshots, sender, secrets,
            new PayloadBuilder(), clock, ids);

        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null,
            new Dictionary<string, string>());

        await sut.FinalizeAndDeliverAsync(id, target);

        var req = sender.Last!;
        Assert.Contains("\"items\":[]", req.Body);
        Assert.Contains("\"count\":1", req.Body);       // unknown_tags.count
        Assert.DoesNotContain("X-EPCF-Signature", string.Join(",", req.Headers.Keys)); // HMAC無効
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.Single(snapshots.Records);
    }
}
