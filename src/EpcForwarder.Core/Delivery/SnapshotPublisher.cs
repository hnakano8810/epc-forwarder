// src/EpcForwarder.Core/Delivery/SnapshotPublisher.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Delivery;

/// <summary>
/// スナップショット1件(SKU集約＋未知タグ分離)を組み立て、ヘッダ/HMACを付けて送信し、記録する共通処理。
/// セッションの状態遷移(finalize/forwarded)は呼び出し側(各Deliverer)が担う。
/// </summary>
public sealed class SnapshotPublisher(
    IReadingStore readings,
    IProductCatalog products,
    ISnapshotStore snapshots,
    IWebhookSender sender,
    ISecretStore secrets,
    PayloadBuilder payloadBuilder,
    IClock clock,
    IIdGenerator ids)
{
    public async Task<WebhookResult> PublishAsync(Session session, string type, bool isFinal, DeliveryTarget target, CancellationToken ct = default)
    {
        var resolved = new List<string>();
        var unknown = new List<string>();
        // PoC assumption: raw-mode EPCs (SearchKey null) land in unknown_tags together with genuine catalog misses.
        foreach (var entry in readings.List(session.PublicId))
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
        // PoC assumption: the snapshot version is consumed before send, so a throw mid-send leaves a version gap.
        var version = snapshots.NextVersion(session.PublicId);
        var idempotencyKey = ids.NewGuid();
        var generatedAt = clock.UtcNow;

        var envelope = new WebhookEnvelope(
            SchemaVersion: target.SchemaVersion,
            Tenant: session.TenantId.ToString(),
            SessionId: session.PublicId,
            BusinessKey: session.BusinessKey,
            Type: type,
            SnapshotVersion: version,
            IsFinal: isFinal,
            IdempotencyKey: idempotencyKey,
            GeneratedAt: generatedAt,
            Items: items,
            UnknownTags: new UnknownTags(unknown));

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
            ["X-EPCF-Is-Final"] = isFinal ? "true" : "false",
            ["X-EPCF-Timestamp"] = timestamp,
        };

        // Fail closed: a configured secret that cannot be resolved must abort the send.
        foreach (var (name, secretRef) in target.Headers)
        {
            var value = await secrets.GetAsync(secretRef, ct)
                ?? throw new InvalidOperationException($"Secret '{secretRef}' for header '{name}' not found.");
            headers[name] = value;
        }

        if (target.HmacEnabled)
        {
            if (target.HmacSecretRef is null)
            {
                throw new InvalidOperationException("HMAC is enabled but no HmacSecretRef is configured.");
            }

            var key = await secrets.GetAsync(target.HmacSecretRef, ct)
                ?? throw new InvalidOperationException($"HMAC secret '{target.HmacSecretRef}' not found.");
            headers["X-EPCF-Signature"] = HmacSigner.Sign(key, timestamp, body);
        }

        var result = await sender.SendAsync(new WebhookRequest(target.Url, target.Method, headers, body), ct);
        snapshots.Record(new SnapshotRecord(session.PublicId, version, isFinal, idempotencyKey, items.Count, result.Success));
        return result;
    }
}
