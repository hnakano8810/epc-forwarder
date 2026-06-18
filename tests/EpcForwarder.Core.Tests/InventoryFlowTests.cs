// tests/EpcForwarder.Core.Tests/InventoryFlowTests.cs
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests;

public class InventoryFlowTests
{
    [Fact]
    public async Task Inventory_Ingest_Provisional_MoreIngest_Finalize()
    {
        // --- 組み立て ---
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snaps = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var publisher = new SnapshotPublisher(readings, products, snaps, sender, secrets, new PayloadBuilder(), clock, ids);
        var inventory = new InventoryDeliverer(sessions, publisher, clock);

        // 同一商品(同一検索キー)・別シリアルの2タグ
        const string epcA = "302DB42318A0038000001231";
        const string epcB = "302DB42318A0038000009999";
        var key = Sgtin96.DeriveSearchKey(epcA);
        products.Add(1, key, "ITEM-AAA");

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "CAMP-1", clock.UtcNow));
        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null,
            new Dictionary<string, string>());

        // --- 取込(epcAを2回読む=後勝ちで1件に収束) + epcB ---
        ingestor.Ingest(id, epcA, "devA", clock.UtcNow, resolveSku: true);
        ingestor.Ingest(id, epcA, "devB", clock.UtcNow, resolveSku: true); // 重複読取 → 上書き
        ingestor.Ingest(id, epcB, "devA", clock.UtcNow, resolveSku: true);

        // --- 仮確定 ---
        await inventory.SendProvisionalAsync(id, target);
        Assert.Equal(SessionStatus.Open, sessions.Get(id)!.Status);
        Assert.Contains("\"is_final\":false", sender.Requests[0].Body);
        Assert.Contains("\"quantity\":2", sender.Requests[0].Body); // epcA(1) + epcB(1)

        // --- 追加取込(同一商品の3タグ目) ---
        ingestor.Ingest(id, "302DB42318A0038000007777", "devA", clock.UtcNow, resolveSku: true);

        // --- 締め切り(確定) ---
        var final = await inventory.FinalizeAndDeliverAsync(id, target);

        // --- 検証 ---
        Assert.True(final.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.Equal(2, sender.Requests.Count);
        Assert.Contains("\"is_final\":true", sender.Requests[1].Body);
        Assert.Contains("\"quantity\":3", sender.Requests[1].Body); // 3タグに増加
        Assert.Equal(new[] { 1, 2 }, snaps.Records.Select(r => r.Version).ToArray());
        Assert.Equal(new[] { false, true }, snaps.Records.Select(r => r.IsFinal).ToArray());
    }
}
