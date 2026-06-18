namespace EpcForwarder.Core.Sessions;

/// <summary>セッション集約。状態遷移 open→finalized→forwarded を強制する。</summary>
public sealed class Session
{
    public Guid PublicId { get; }
    public int TenantId { get; }
    public SessionType Type { get; }
    public string? BusinessKey { get; }
    public SessionStatus Status { get; private set; }
    public int? ExpectedCount { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastEventAt { get; private set; }
    public DateTimeOffset? FinalizedAt { get; private set; }
    public DateTimeOffset? ForwardedAt { get; private set; }

    public Session(Guid publicId, int tenantId, SessionType type, string? businessKey, DateTimeOffset now)
    {
        PublicId = publicId;
        TenantId = tenantId;
        Type = type;
        BusinessKey = businessKey;
        Status = SessionStatus.Open;
        CreatedAt = now;
        LastEventAt = now;
    }

    public void Touch(DateTimeOffset now) => LastEventAt = now;

    public void SetExpectedCount(int count) => ExpectedCount = count;

    public void Finalize(DateTimeOffset now)
    {
        if (Status != SessionStatus.Open)
        {
            throw new InvalidOperationException($"Cannot finalize a session in status {Status}.");
        }

        Status = SessionStatus.Finalized;
        FinalizedAt = now;
        LastEventAt = now;
    }

    public void MarkForwarded(DateTimeOffset now)
    {
        if (Status != SessionStatus.Finalized)
        {
            throw new InvalidOperationException($"Cannot mark forwarded from status {Status}.");
        }

        Status = SessionStatus.Forwarded;
        ForwardedAt = now;
        LastEventAt = now;
    }
}
