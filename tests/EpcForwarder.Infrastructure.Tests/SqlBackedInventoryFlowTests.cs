// tests/EpcForwarder.Infrastructure.Tests/SqlBackedInventoryFlowTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlBackedInventoryFlowTests(SqlServerFixture fx)
{
    private sealed class StubSender : IWebhookSender
    {
        public List<WebhookRequest> Requests { get; } = new();
        public Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WebhookResult(true, 200));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class SeqIds : IIdGenerator { private int _n; public Guid NewGuid() => new($"00000000-0000-0000-0000-{(++_n):D12}"); }
    private sealed class NoSecrets : ISecretStore { public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null); }

    // GS1 mod-10 check digit helper (body13 = indicator + 12 digits)
    private static string Gtin14(string body13)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = body13[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d;
        }
        return body13 + (10 - (sum % 10)) % 10;
    }

    [Fact]
    public async Task Inventory_OverSql_Register_Ingest_Provisional_Finalize()
    {
        var cf = new SqlConnectionFactory(fx.ConnectionString);
        var sessions = new SqlSessionStore(cf);
        var readings = new SqlReadingStore(cf);
        var snaps = new SqlSnapshotStore(cf);
        var products = new SqlProductStore(cf);
        var sender = new StubSender();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SeqIds();

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var publisher = new SnapshotPublisher(readings, products, snaps, sender, new NoSecrets(), new PayloadBuilder(), clock, ids);
        var inventory = new InventoryDeliverer(sessions, publisher, clock);
        var registrar = new ProductRegistrar(products);

        var tenant = fx.NewTenant();
        var gtin = Gtin14("0000000000001"); // 共有ヘルパ(Core.Tests)と同じ規則
        var key = registrar.Register(tenant, gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        var sid = Guid.NewGuid();
        sessions.Save(new Session(sid, tenant, SessionType.Inventory, "CAMP-1", clock.UtcNow));

        // 実タグ(同一商品・別シリアル)。登録キーにシリアルを載せたEPC。
        var tagBytes = Convert.FromHexString(key); tagBytes[11] = 0x2A;
        var epc1 = Convert.ToHexString(tagBytes);
        tagBytes[11] = 0x2B; var epc2 = Convert.ToHexString(tagBytes);

        ingestor.Ingest(sid, epc1, "devA", clock.UtcNow, resolveSku: true);
        ingestor.Ingest(sid, epc1, "devB", clock.UtcNow, resolveSku: true); // 後勝ち→1件
        ingestor.Ingest(sid, epc2, "devA", clock.UtcNow, resolveSku: true);

        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null, new Dictionary<string, string>());

        await inventory.SendProvisionalAsync(sid, target);
        Assert.Equal(SessionStatus.Open, sessions.Get(sid)!.Status);

        var final = await inventory.FinalizeAndDeliverAsync(sid, target);

        Assert.True(final.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(sid)!.Status);
        Assert.Equal(2, sender.Requests.Count);
        Assert.Contains("\"is_final\":false", sender.Requests[0].Body);
        Assert.Contains("\"is_final\":true", sender.Requests[1].Body);
        Assert.Contains("\"sku\":\"ITEM-AAA\"", sender.Requests[1].Body);
        Assert.Contains("\"quantity\":2", sender.Requests[1].Body); // epc1+epc2 が同一SKUに集約
    }
}
