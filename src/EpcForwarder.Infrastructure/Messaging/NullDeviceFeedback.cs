using EpcForwarder.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EpcForwarder.Infrastructure.Messaging;

/// <summary>
/// 端末フィードバックのプレースホルダ。実C2D配信は宛先デバイスIDの特定設計が必要なため未実装(③b以降)。
/// 当面はログ出力のみ。
/// </summary>
public sealed class NullDeviceFeedback(ILogger<NullDeviceFeedback> logger) : IDeviceFeedback
{
    public Task SendReachabilityAsync(Guid sessionId, ReachabilityResult result, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Reachability feedback (no-op): session={SessionId} expected={Expected} received={Received}",
            sessionId, result.Expected, result.Received);
        return Task.CompletedTask;
    }
}
