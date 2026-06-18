// src/EpcForwarder.Infrastructure/Persistence/SqlSnapshotStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlSnapshotStore(SqlConnectionFactory factory) : ISnapshotStore
{
    // PoC: MAX(version)+1 のベストエフォート(セッション単位=単一ライタ想定)。競合は UQ_snapshot_ver が検出。
    public int NextVersion(Guid sessionId)
    {
        using var conn = factory.Create();
        return conn.ExecuteScalar<int>(
            "SELECT ISNULL(MAX(version),0)+1 FROM dbo.snapshot WHERE session_id = @sessionId",
            new { sessionId });
    }

    public void Record(SnapshotRecord record)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            INSERT INTO dbo.snapshot (tenant_id, session_id, version, is_final, idempotency_key, item_count, success)
            VALUES (0, @SessionId, @Version, @IsFinal, @IdempotencyKey, @ItemCount, @Success)
            """,
            new
            {
                record.SessionId,
                record.Version,
                record.IsFinal,
                record.IdempotencyKey,
                record.ItemCount,
                record.Success,
            });
    }
}
