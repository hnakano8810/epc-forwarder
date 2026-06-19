// src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlReadingStore(SqlConnectionFactory factory) : IReadingStore
{
    public void Upsert(Guid sessionId, ReadingEntry entry)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            MERGE dbo.reading WITH (HOLDLOCK) AS t
            USING (SELECT @SessionId AS session_id, @Epc AS epc) AS s
               ON t.session_id = s.session_id AND t.epc = s.epc
            WHEN MATCHED THEN UPDATE SET
               search_key = @SearchKey, device_id = @DeviceId, read_at = @ReadAt,
               location_l1 = @L1, location_l2 = @L2, location_l3 = @L3,
               updated_at = SYSDATETIMEOFFSET(), excluded = 0
            WHEN NOT MATCHED THEN INSERT
               (session_id, tenant_id, epc, search_key, device_id, read_at, location_l1, location_l2, location_l3)
               VALUES (@SessionId,
                       -- Session は ReadingIngestor.Ingest が存在保証済み。未存在なら NOT NULL 違反で fail-fast(正しい挙動)。
                       (SELECT tenant_id FROM dbo.session WHERE public_id = @SessionId),
                       @Epc, @SearchKey, @DeviceId, @ReadAt, @L1, @L2, @L3);
            """,
            new
            {
                SessionId = sessionId,
                Epc = Convert.FromHexString(entry.Epc),
                SearchKey = entry.SearchKey is null ? null : Convert.FromHexString(entry.SearchKey),
                entry.DeviceId,
                ReadAt = entry.ReadAt,
                L1 = entry.Location?.L1,
                L2 = entry.Location?.L2,
                L3 = entry.Location?.L3,
            });
    }

    public IReadOnlyList<ReadingEntry> List(Guid sessionId)
    {
        using var conn = factory.Create();
        var rows = conn.Query<ReadingRow>(
            """
            SELECT epc AS Epc, search_key AS SearchKey, device_id AS DeviceId, read_at AS ReadAt,
                   location_l1 AS L1, location_l2 AS L2, location_l3 AS L3
            FROM dbo.reading WHERE session_id = @sessionId AND excluded = 0
            """, new { sessionId });

        return rows.Select(r => new ReadingEntry(
            Convert.ToHexString(r.Epc),
            r.SearchKey is null ? null : Convert.ToHexString(r.SearchKey),
            r.DeviceId,
            r.ReadAt,
            r.L1 is null && r.L2 is null && r.L3 is null ? null : new ReadLocation(r.L1, r.L2, r.L3))).ToList();
    }

    public int CountUnique(Guid sessionId)
    {
        using var conn = factory.Create();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.reading WHERE session_id = @sessionId AND excluded = 0",
            new { sessionId });
    }

    private sealed class ReadingRow
    {
        public byte[] Epc { get; init; } = [];
        public byte[]? SearchKey { get; init; }
        public string? DeviceId { get; init; }
        public DateTimeOffset ReadAt { get; init; }
        public string? L1 { get; init; }
        public string? L2 { get; init; }
        public string? L3 { get; init; }
    }
}
