// src/EpcForwarder.Core/Sessions/ReadingIngestor.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Ingestion;

namespace EpcForwarder.Core.Sessions;

/// <summary>読取をセッションへ取り込む。SKU解決時は EPC&Mask で検索キーを付与。後勝ちはストアが担う。</summary>
public sealed class ReadingIngestor(ISessionStore sessions, IReadingStore readings, IClock clock)
{
    public void Ingest(Guid sessionId, string epcHex, string? deviceId, DateTimeOffset readAt, bool resolveSku, ReadLocation? location = null)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        var searchKey = resolveSku ? Sgtin96.DeriveSearchKey(epcHex) : null;
        readings.UpsertBatch(sessionId, [new ReadingEntry(epcHex, searchKey, deviceId, readAt, location)]);

        session.Touch(clock.UtcNow);
        sessions.Save(session);
    }

    /// <summary>
    /// 読取バッチをセッションへ一括取り込み。同一EPCは後勝ち(最大 read_at)で1件に dedup してから一括 UPSERT する
    /// (MERGE のソース重複エラー回避＝後勝ち UPSERT と意味論一致)。SKU解決・session Touch は各1回。
    /// </summary>
    public void IngestBatch(Guid sessionId, IReadOnlyList<ReadEntry> reads, string? deviceId, bool resolveSku)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        // 後勝ち dedup: 同一EPCは最大 read_at の要素を採用。
        var deduped = reads
            .GroupBy(r => r.Epc)
            .Select(g => g.OrderByDescending(r => r.ReadAt).First())
            .Select(r => new ReadingEntry(
                r.Epc,
                resolveSku ? Sgtin96.DeriveSearchKey(r.Epc) : null,
                deviceId,
                r.ReadAt,
                r.Location))
            .ToList();

        if (deduped.Count > 0)
        {
            readings.UpsertBatch(sessionId, deduped);
        }

        session.Touch(clock.UtcNow);
        sessions.Save(session);
    }
}
