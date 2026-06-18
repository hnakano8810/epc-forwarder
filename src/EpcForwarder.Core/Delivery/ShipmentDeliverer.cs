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

/// <summary>伝票の確定→集約→送信→forwarded。配信本体は SnapshotPublisher に委譲。</summary>
public sealed class ShipmentDeliverer(ISessionStore sessions, SnapshotPublisher publisher, IClock clock)
{
    public async Task<WebhookResult> FinalizeAndDeliverAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        if (session.Status == SessionStatus.Forwarded)
        {
            throw new InvalidOperationException($"Session {sessionId} is already forwarded; use the re-send flow.");
        }

        // Finalize is idempotent + persisted so a failed send can be retried without throwing.
        if (session.Status == SessionStatus.Open)
        {
            session.Finalize(clock.UtcNow);
            sessions.Save(session);
        }

        var result = await publisher.PublishAsync(session, "shipment", isFinal: true, target, ct);

        if (result.Success)
        {
            session.MarkForwarded(clock.UtcNow);
            sessions.Save(session);
        }

        return result;
    }
}
