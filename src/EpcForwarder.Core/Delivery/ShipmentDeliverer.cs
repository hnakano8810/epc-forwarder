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

        if (session.Status == SessionStatus.Forwarded)
            throw new InvalidOperationException($"Session {sessionId} is already forwarded; use the re-send flow.");

        // Finalize is idempotent + persisted so a failed send can be retried without throwing.
        // Re-sending an already-Forwarded session is out of scope here (handled by the separate re-send flow later).
        if (session.Status == SessionStatus.Open)
        {
            session.Finalize(clock.UtcNow);
            sessions.Save(session); // persist FinalizedAt regardless of send outcome (enables retry)
        }

        var resolved = new List<string>();
        var unknown = new List<string>();
        // PoC assumption: raw-mode EPCs (SearchKey null) land in unknown_tags together with genuine
        // catalog misses. Not exercised in the shipment slice since resolveSku=true.
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
        // PoC assumption: the snapshot version is consumed before send, so a throw mid-send leaves a version gap.
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
            ["X-EPCF-Is-Final"] = "true",
            ["X-EPCF-Timestamp"] = timestamp,
        };

        // Fail closed: a configured secret that cannot be resolved must abort the send, never send without it.
        foreach (var (name, secretRef) in target.Headers)
        {
            var value = await secrets.GetAsync(secretRef, ct)
                ?? throw new InvalidOperationException($"Secret '{secretRef}' for header '{name}' not found.");
            headers[name] = value;
        }

        if (target.HmacEnabled)
        {
            if (target.HmacSecretRef is null)
                throw new InvalidOperationException("HMAC is enabled but no HmacSecretRef is configured.");

            var key = await secrets.GetAsync(target.HmacSecretRef, ct)
                ?? throw new InvalidOperationException($"HMAC secret '{target.HmacSecretRef}' not found.");
            headers["X-EPCF-Signature"] = HmacSigner.Sign(key, timestamp, body);
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
