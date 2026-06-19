using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Inventory;

/// <summary>仮確定/確定の結果(ホスト側のレスポンス用)。Delivered=false は有効宛先なし。</summary>
public sealed record InventoryPublishOutcome(bool Delivered, WebhookResult? Delivery);

/// <summary>
/// 棚卸の HTTP 起動(仮確定/確定)。tenant で session を解決し、有効宛先(先頭)へ配信する。
/// session 不在 or tenant 不一致は null(呼び出し側で 404)。
/// 状態不正(仮確定: open でない / 確定: 既 forwarded)は InventoryDeliverer が InvalidOperationException を投げる(呼び出し側で 409)。
/// PoC: 複数宛先のファンアウトは未対応(先頭のみ)。
/// </summary>
public sealed class InventoryDispatcher(
    ISessionStore sessions,
    InventoryDeliverer deliverer,
    IDestinationCatalog destinations)
{
    public Task<InventoryPublishOutcome?> SendProvisionalAsync(int tenantId, Guid sessionId, CancellationToken ct = default) =>
        PublishAsync(tenantId, sessionId, (target, c) => deliverer.SendProvisionalAsync(sessionId, target, c), ct);

    public Task<InventoryPublishOutcome?> FinalizeAndDeliverAsync(int tenantId, Guid sessionId, CancellationToken ct = default) =>
        PublishAsync(tenantId, sessionId, (target, c) => deliverer.FinalizeAndDeliverAsync(sessionId, target, c), ct);

    private async Task<InventoryPublishOutcome?> PublishAsync(
        int tenantId,
        Guid sessionId,
        Func<DeliveryTarget, CancellationToken, Task<WebhookResult>> publish,
        CancellationToken ct)
    {
        var session = sessions.Get(sessionId);
        if (session is null || session.TenantId != tenantId)
        {
            return null;
        }

        var target = destinations.GetActiveTargets(tenantId).FirstOrDefault();
        if (target is null)
        {
            return new InventoryPublishOutcome(Delivered: false, Delivery: null);
        }

        var result = await publish(target, ct);
        return new InventoryPublishOutcome(Delivered: true, Delivery: result);
    }
}
