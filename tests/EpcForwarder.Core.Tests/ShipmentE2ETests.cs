// tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs
using System.Net;
using System.Text;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using EpcForwarder.Infrastructure.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests;

public class ShipmentE2ETests
{
    private sealed class LoopbackResolver : IHostResolver
    {
        public IReadOnlyList<IPAddress> Resolve(string host) => new[] { IPAddress.Loopback };
    }

    [Fact]
    public async Task Shipment_Ingest_Complete_Finalize_DeliversAggregatedPayload()
    {
        // --- 受信側(連携先)スタブ ---
        using var listener = new HttpListener();
        const string url = "http://127.0.0.1:18790/hook/";
        listener.Prefixes.Add(url);
        listener.Start();
        string? body = null;
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        // --- 組み立て ---
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snapshots = new InMemorySnapshotStore();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        using var http = new HttpClient();

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var reconciler = new ShipmentReconciler(sessions, readings, new CapturingDeviceFeedback(), clock);
        var deliverer = new ShipmentDeliverer(sessions, readings, products, snapshots,
            new HttpWebhookSender(http), secrets, new PayloadBuilder(), clock, ids);

        const string epcA = "302DB42318A0038000001231";
        const string epcB = "302DB42318A0038000009999"; // 同一検索キー
        var key = Sgtin96.DeriveSearchKey(epcA);
        products.Add(1, key, "ITEM-AAA");

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));

        // --- フロー ---
        ingestor.Ingest(id, epcA, "devA", clock.UtcNow, resolveSku: true);
        ingestor.Ingest(id, epcB, "devA", clock.UtcNow, resolveSku: true);

        var reach = await reconciler.CompleteAsync(id, expectedCount: 2);
        Assert.True(reach.IsMatch);

        // 送信前にURLガード（ローカル許可）
        WebhookUrlGuard.Validate(url, new WebhookUrlGuardOptions(AllowHttp: true, AllowPrivateNetworks: true), new LoopbackResolver());

        var target = new DeliveryTarget(url, "POST", "1", HmacEnabled: false, HmacSecretRef: null,
            Headers: new Dictionary<string, string>());
        var result = await deliverer.FinalizeAndDeliverAsync(id, target);

        await server.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        // --- 検証 ---
        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.NotNull(body);
        Assert.Contains("\"sku\":\"ITEM-AAA\"", body);
        Assert.Contains("\"quantity\":2", body);
        Assert.Contains("\"is_final\":true", body);
    }
}
