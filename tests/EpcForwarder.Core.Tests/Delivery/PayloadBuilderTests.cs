using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class PayloadBuilderTests
{
    [Fact]
    public void Serialize_UsesSnakeCase_AndContainsItems()
    {
        var envelope = new WebhookEnvelope(
            SchemaVersion: "1",
            Tenant: "acme",
            SessionId: Guid.Parse("9c3a8f10-0000-0000-0000-000000000000"),
            BusinessKey: "DN-2026-000123",
            Type: "shipment",
            SnapshotVersion: 3,
            IsFinal: true,
            IdempotencyKey: Guid.Parse("f1e2d3c4-0000-0000-0000-000000000000"),
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Items: new[] { new AggregateItem("ITEM-AAA", 2) },
            UnknownTags: new UnknownTags(0, Array.Empty<string>()));

        var json = new PayloadBuilder().Serialize(envelope);

        Assert.Contains("\"schema_version\":\"1\"", json);
        Assert.Contains("\"snapshot_version\":3", json);
        Assert.Contains("\"is_final\":true", json);
        Assert.Contains("\"idempotency_key\":\"f1e2d3c4", json);
        Assert.Contains("\"sku\":\"ITEM-AAA\"", json);
        Assert.Contains("\"quantity\":2", json);
    }
}
