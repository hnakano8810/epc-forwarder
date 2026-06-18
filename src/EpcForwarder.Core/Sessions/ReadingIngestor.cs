// src/EpcForwarder.Core/Sessions/ReadingIngestor.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;

namespace EpcForwarder.Core.Sessions;

/// <summary>読取1件をセッションへ取り込む。SKU解決時は EPC&Mask で検索キーを付与。後勝ちはストアが担う。</summary>
public sealed class ReadingIngestor(ISessionStore sessions, IReadingStore readings, IClock clock)
{
    public void Ingest(Guid sessionId, string epcHex, string? deviceId, DateTimeOffset readAt, bool resolveSku, ReadLocation? location = null)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        var searchKey = resolveSku ? Sgtin96.DeriveSearchKey(epcHex) : null;
        readings.Upsert(sessionId, new ReadingEntry(epcHex, searchKey, deviceId, readAt, location));

        session.Touch(clock.UtcNow);
        sessions.Save(session);
    }
}
