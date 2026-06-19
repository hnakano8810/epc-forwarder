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
    // Task 4 で CompleteAsync が利用する。一次コンストラクターの警告抑制を兼ねてフィールド化。
    private readonly ShipmentReconciler _reconciler = reconciler;
    private readonly ShipmentDeliverer _deliverer = deliverer;
    private readonly IDestinationCatalog _destinations = destinations;

    public void IngestRead(ReadCommand cmd)
    {
        // 遅延生成: 未知セッションはメッセージのメタデータで作成(既存は触らない)。
        if (sessions.Get(cmd.SessionId) is null)
        {
            sessions.Save(new Session(cmd.SessionId, cmd.Tenant, cmd.SessionType, cmd.BusinessKey, clock.UtcNow));
        }

        ingestor.Ingest(cmd.SessionId, cmd.Epc, cmd.DeviceId, cmd.ReadAt, cmd.ResolveSku, cmd.Location);
    }
}
