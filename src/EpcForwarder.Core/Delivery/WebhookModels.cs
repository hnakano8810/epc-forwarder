namespace EpcForwarder.Core.Delivery;

public sealed record AggregateItem(string Sku, int Quantity);

public sealed record UnknownTags(int Count, IReadOnlyList<string> Epcs);

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
