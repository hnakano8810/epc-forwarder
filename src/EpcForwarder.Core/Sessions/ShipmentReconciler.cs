// src/EpcForwarder.Core/Sessions/ShipmentReconciler.cs
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Core.Sessions;

/// <summary>伝票完了イベントの到達性突合（システム到達性検証）。不一致時のみ再読取をフィードバック。</summary>
public sealed class ShipmentReconciler(
    ISessionStore sessions,
    IReadingStore readings,
    IDeviceFeedback feedback,
    IClock clock)
{
    public async Task<ReachabilityResult> CompleteAsync(Guid sessionId, int expectedCount, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        session.SetExpectedCount(expectedCount);
        var received = readings.CountUnique(sessionId);
        var result = new ReachabilityResult(expectedCount, received);

        if (!result.IsMatch)
        {
            await feedback.SendReachabilityAsync(sessionId, result, ct);
        }

        session.Touch(clock.UtcNow);
        sessions.Save(session);
        return result;
    }
}
