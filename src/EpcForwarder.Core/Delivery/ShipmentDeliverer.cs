// src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Delivery;

/// <summary>宛先設定の実行時投影。機密値は SecretRef を ISecretStore で解決する。</summary>
public sealed record DeliveryTarget(
    string Url,
    string Method,
    string SchemaVersion,
    bool HmacEnabled,
    string? HmacSecretRef,
    IReadOnlyDictionary<string, string> Headers); // headerName -> secretRef

/// <summary>伝票の確定→SKU集約→ペイロード組立→署名→送信→スナップショット記録→forwarded。</summary>
public sealed class ShipmentDeliverer(
    ISessionStore sessions,
    IReadingStore readings,
    IProductCatalog products,
    ISnapshotStore snapshots,
    IWebhookSender sender,
    ISecretStore secrets,
    PayloadBuilder payloadBuilder,
    IClock clock,
    IIdGenerator ids)
{
    public async Task<WebhookResult> FinalizeAndDeliverAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        session.Finalize(clock.UtcNow);

        var resolved = new List<string>();
        var unknown = new List<string>();
        foreach (var entry in readings.List(sessionId))
        {
            var sku = entry.SearchKey is null ? null : products.ResolveSku(session.TenantId, entry.SearchKey);
            if (sku is null)
            {
                unknown.Add(entry.Epc);
            }
            else
            {
                resolved.Add(sku);
            }
        }

        var items = SkuAggregator.Aggregate(resolved);
        var version = snapshots.NextVersion(sessionId);
        var idempotencyKey = ids.NewGuid();
        var generatedAt = clock.UtcNow;

        var envelope = new WebhookEnvelope(
            SchemaVersion: target.SchemaVersion,
            Tenant: session.TenantId.ToString(),
            SessionId: session.PublicId,
            BusinessKey: session.BusinessKey,
            Type: "shipment",
            SnapshotVersion: version,
            IsFinal: true,
            IdempotencyKey: idempotencyKey,
            GeneratedAt: generatedAt,
            Items: items,
            UnknownTags: new UnknownTags(unknown.Count, unknown));

        var body = payloadBuilder.Serialize(envelope);
        var timestamp = generatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Idempotency-Key"] = idempotencyKey.ToString(),
            ["X-EPCF-Tenant"] = session.TenantId.ToString(),
            ["X-EPCF-Session"] = session.PublicId.ToString(),
            ["X-EPCF-Snapshot-Version"] = version.ToString(),
            ["X-EPCF-Schema-Version"] = target.SchemaVersion,
            ["X-EPCF-Is-Final"] = "true",
            ["X-EPCF-Timestamp"] = timestamp,
        };

        foreach (var (name, secretRef) in target.Headers)
        {
            var value = await secrets.GetAsync(secretRef, ct);
            if (value is not null)
            {
                headers[name] = value;
            }
        }

        if (target.HmacEnabled && target.HmacSecretRef is not null)
        {
            var key = await secrets.GetAsync(target.HmacSecretRef, ct);
            if (key is not null)
            {
                headers["X-EPCF-Signature"] = HmacSigner.Sign(key, timestamp, body);
            }
        }

        var result = await sender.SendAsync(new WebhookRequest(target.Url, target.Method, headers, body), ct);
        snapshots.Record(new SnapshotRecord(sessionId, version, IsFinal: true, idempotencyKey, items.Count, result.Success));

        if (result.Success)
        {
            session.MarkForwarded(clock.UtcNow);
            sessions.Save(session);
        }

        return result;
    }
}
