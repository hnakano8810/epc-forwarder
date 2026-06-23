using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Ingestion;

/// <summary>
/// 取込メッセージをドメイン操作へ振り分ける。読取は未知セッションを遅延生成してから取り込み、
/// 伝票の完了は到達性突合→一致時に有効な宛先へ確定配信する。
/// </summary>
public sealed class IngestionDispatcher(
    ISessionStore sessions,
    ReadingIngestor ingestor,
    ShipmentReconciler reconciler,
    ShipmentDeliverer deliverer,
    IDestinationCatalog destinations,
    IClock clock)
{
    public void IngestReads(ReadBatchCommand cmd)
    {
        // 遅延生成: 未知セッションはメッセージのメタデータで作成(既存は触らない)。バッチで1回。
        if (sessions.Get(cmd.SessionId) is null)
        {
            sessions.Save(new Session(cmd.SessionId, cmd.Tenant, cmd.SessionType, cmd.BusinessKey, clock.UtcNow));
        }

        ingestor.IngestBatch(cmd.SessionId, cmd.Reads, cmd.DeviceId, cmd.ResolveSku);
    }

    /// <summary>
    /// 伝票完了。到達性を突合し、一致時のみ有効な宛先(先頭)へ確定配信する。
    /// PoC: 複数宛先のファンアウトは未対応(ShipmentDeliverer がセッション単位で1回 forwarded にするため先頭のみ)。
    /// 不一致時は ShipmentReconciler が再読取フィードバックを送る(本メソッドは配信しない)。
    /// </summary>
    public async Task<CompletionOutcome> CompleteAsync(CompleteCommand cmd, CancellationToken ct = default)
    {
        var reachability = await reconciler.CompleteAsync(cmd.SessionId, cmd.ExpectedCount, ct);
        if (!reachability.IsMatch)
        {
            return new CompletionOutcome(reachability, Delivered: false, Delivery: null);
        }

        // at-least-once 重複: 既に配信済みなら冪等に成功扱い(再 finalize で throw させない)。
        // Delivered=true だが Delivery は null(この経路では新たな送信を行わないため)。
        if (sessions.Get(cmd.SessionId)?.Status == SessionStatus.Forwarded)
        {
            return new CompletionOutcome(reachability, Delivered: true, Delivery: null);
        }

        var target = destinations.GetActiveTargets(cmd.Tenant).FirstOrDefault();
        if (target is null)
        {
            return new CompletionOutcome(reachability, Delivered: false, Delivery: null);
        }

        var delivery = await deliverer.FinalizeAndDeliverAsync(cmd.SessionId, target, ct);
        return new CompletionOutcome(reachability, Delivered: true, Delivery: delivery);
    }
}
