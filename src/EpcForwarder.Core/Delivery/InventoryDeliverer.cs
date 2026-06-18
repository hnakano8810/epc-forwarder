// src/EpcForwarder.Core/Delivery/InventoryDeliverer.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Delivery;

/// <summary>
/// 棚卸の配信。締め切り前の仮確定(セッションはopen維持・繰り返し可)と、締め切りの確定(forwarded)を提供する。
/// 件数突合は行わない(基本設計2.2)。配信本体は SnapshotPublisher に委譲。
/// </summary>
public sealed class InventoryDeliverer(ISessionStore sessions, SnapshotPublisher publisher, IClock clock)
{
    /// <summary>締め切り前に現時点の集約スナップショットを送る。セッションは open のまま。</summary>
    public async Task<WebhookResult> SendProvisionalAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        if (session.Status != SessionStatus.Open)
        {
            throw new InvalidOperationException($"Provisional send requires an open session (was {session.Status}).");
        }

        var result = await publisher.PublishAsync(session, "inventory", isFinal: false, target, ct);
        session.Touch(clock.UtcNow);
        sessions.Save(session);
        return result;
    }

    /// <summary>締め切り。確定スナップショットを送り forwarded にする。</summary>
    public async Task<WebhookResult> FinalizeAndDeliverAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        if (session.Status == SessionStatus.Forwarded)
        {
            throw new InvalidOperationException($"Session {sessionId} is already forwarded; use the re-send flow.");
        }

        if (session.Status == SessionStatus.Open)
        {
            session.Finalize(clock.UtcNow);
            sessions.Save(session);
        }

        var result = await publisher.PublishAsync(session, "inventory", isFinal: true, target, ct);

        if (result.Success)
        {
            session.MarkForwarded(clock.UtcNow);
            sessions.Save(session);
        }

        return result;
    }
}
