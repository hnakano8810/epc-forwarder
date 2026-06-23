// src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs
using System.Data;
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlReadingStore(SqlConnectionFactory factory) : IReadingStore
{
    public void Upsert(Guid sessionId, ReadingEntry entry) => UpsertBatch(sessionId, [entry]);

    public void UpsertBatch(Guid sessionId, IReadOnlyList<ReadingEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        using var conn = factory.Create();
        conn.Execute(
            """
            MERGE dbo.reading WITH (HOLDLOCK) AS t
            USING @rows AS s
               ON t.session_id = @SessionId AND t.epc = s.epc
            WHEN MATCHED THEN UPDATE SET
               search_key = s.search_key, device_id = s.device_id, read_at = s.read_at,
               location_l1 = s.location_l1, location_l2 = s.location_l2, location_l3 = s.location_l3,
               updated_at = SYSDATETIMEOFFSET(), excluded = 0
            WHEN NOT MATCHED THEN INSERT
               (session_id, tenant_id, epc, search_key, device_id, read_at, location_l1, location_l2, location_l3)
               VALUES (@SessionId,
                       -- Session は ReadingIngestor が存在保証済み。未存在なら NOT NULL 違反で fail-fast(正しい挙動)。
                       (SELECT tenant_id FROM dbo.session WHERE public_id = @SessionId),
                       s.epc, s.search_key, s.device_id, s.read_at, s.location_l1, s.location_l2, s.location_l3);
            """,
            new
            {
                SessionId = sessionId,
                rows = ToTvp(entries).AsTableValuedParameter("dbo.ReadingTvp"),
            });
    }

    /// <summary>ReadingEntry 群を dbo.ReadingTvp 互換の DataTable へ。列の順序・型は型定義に一致させる。</summary>
    private static DataTable ToTvp(IReadOnlyList<ReadingEntry> entries)
    {
        var table = new DataTable();
        table.Columns.Add("epc", typeof(byte[]));
        table.Columns.Add("search_key", typeof(byte[]));
        table.Columns.Add("device_id", typeof(string));
        table.Columns.Add("read_at", typeof(DateTimeOffset));
        table.Columns.Add("location_l1", typeof(string));
        table.Columns.Add("location_l2", typeof(string));
        table.Columns.Add("location_l3", typeof(string));

        foreach (var e in entries)
        {
            table.Rows.Add(
                Convert.FromHexString(e.Epc),
                e.SearchKey is null ? (object)DBNull.Value : Convert.FromHexString(e.SearchKey),
                (object?)e.DeviceId ?? DBNull.Value,
                e.ReadAt,
                (object?)e.Location?.L1 ?? DBNull.Value,
                (object?)e.Location?.L2 ?? DBNull.Value,
                (object?)e.Location?.L3 ?? DBNull.Value);
        }

        return table;
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
