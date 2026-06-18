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
               updated_at = SYSDATETIMEOFFSET(), excluded = 0
            WHEN NOT MATCHED THEN INSERT
               (session_id, tenant_id, epc, search_key, device_id, read_at)
               VALUES (@SessionId, 0, @Epc, @SearchKey, @DeviceId, @ReadAt);
            """,
            new
            {
                SessionId = sessionId,
                Epc = Convert.FromHexString(entry.Epc),
                SearchKey = entry.SearchKey is null ? null : Convert.FromHexString(entry.SearchKey),
                entry.DeviceId,
                ReadAt = entry.ReadAt,
            });
    }

    public IReadOnlyList<ReadingEntry> List(Guid sessionId)
    {
        using var conn = factory.Create();
        var rows = conn.Query<ReadingRow>(
            """
            SELECT epc AS Epc, search_key AS SearchKey, device_id AS DeviceId, read_at AS ReadAt
            FROM dbo.reading WHERE session_id = @sessionId AND excluded = 0
            """, new { sessionId });

        return rows.Select(r => new ReadingEntry(
            Convert.ToHexString(r.Epc),
            r.SearchKey is null ? null : Convert.ToHexString(r.SearchKey),
            r.DeviceId,
            r.ReadAt)).ToList();
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
    }
}
