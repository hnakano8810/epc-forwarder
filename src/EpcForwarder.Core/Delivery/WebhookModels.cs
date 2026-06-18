namespace EpcForwarder.Core.Delivery;

public sealed record AggregateItem(string Sku, int Quantity);

public sealed record UnknownTags(IReadOnlyList<string> Epcs)
{
    public int Count => Epcs.Count;
}

public sealed record WebhookEnvelope(
    string SchemaVersion,
    string Tenant,
    Guid SessionId,
    string? BusinessKey,
    string Type,
    int SnapshotVersion,
    bool IsFinal,
    Guid IdempotencyKey,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AggregateItem> Items,
    UnknownTags UnknownTags);
