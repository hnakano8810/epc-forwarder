// src/EpcForwarder.Infrastructure/Persistence/SqlSessionStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlSessionStore(SqlConnectionFactory factory) : ISessionStore
{
    public Session? Get(Guid publicId)
    {
        using var conn = factory.Create();
        var row = conn.QuerySingleOrDefault<SessionRow>(
            """
            SELECT public_id AS PublicId, tenant_id AS TenantId, type AS Type, business_key AS BusinessKey,
                   status AS Status, expected_count AS ExpectedCount, created_at AS CreatedAt,
                   last_event_at AS LastEventAt, finalized_at AS FinalizedAt, forwarded_at AS ForwardedAt
            FROM dbo.session WHERE public_id = @publicId
            """, new { publicId });

        return row is null
            ? null
            : Session.Rehydrate(
                row.PublicId, row.TenantId, Enum.Parse<SessionType>(row.Type, ignoreCase: true), row.BusinessKey,
                Enum.Parse<SessionStatus>(row.Status, ignoreCase: true), row.ExpectedCount,
                row.CreatedAt, row.LastEventAt, row.FinalizedAt, row.ForwardedAt);
    }

    public void Save(Session session)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            MERGE dbo.session AS t
            USING (SELECT @PublicId AS public_id) AS s ON t.public_id = s.public_id
            WHEN MATCHED THEN UPDATE SET
                status = @Status, expected_count = @ExpectedCount, last_event_at = @LastEventAt,
                finalized_at = @FinalizedAt, forwarded_at = @ForwardedAt
            WHEN NOT MATCHED THEN INSERT
                (public_id, tenant_id, type, business_key, status, expected_count, created_at, last_event_at, finalized_at, forwarded_at)
                VALUES (@PublicId, @TenantId, @Type, @BusinessKey, @Status, @ExpectedCount, @CreatedAt, @LastEventAt, @FinalizedAt, @ForwardedAt);
            """,
            new
            {
                session.PublicId,
                session.TenantId,
                Type = session.Type.ToString(),
                session.BusinessKey,
                Status = session.Status.ToString(),
                session.ExpectedCount,
                CreatedAt = session.CreatedAt,
                LastEventAt = session.LastEventAt,
                FinalizedAt = session.FinalizedAt,
                ForwardedAt = session.ForwardedAt,
            });
    }

    private sealed class SessionRow
    {
        public Guid PublicId { get; init; }
        public int TenantId { get; init; }
        public string Type { get; init; } = "";
        public string? BusinessKey { get; init; }
        public string Status { get; init; } = "";
        public int? ExpectedCount { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastEventAt { get; init; }
        public DateTimeOffset? FinalizedAt { get; init; }
        public DateTimeOffset? ForwardedAt { get; init; }
    }
}
