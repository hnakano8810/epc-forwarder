using System.Net;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface IHostResolver
{
    IReadOnlyList<IPAddress> Resolve(string host);
}

public interface IIdGenerator { Guid NewGuid(); }

public interface ISessionStore
{
    Session? Get(Guid publicId);
    void Save(Session session);
}

/// <summary>テナント識別コード(tenant.code)を内部 tenant_id に解決する。未知なら null。</summary>
public interface ITenantLookup
{
    int? ResolveId(string code);
}

/// <summary>読取に付随するロケーション文脈(棚卸のロケ別集計に使用)。全要素任意。</summary>
public sealed record ReadLocation(string? L1, string? L2, string? L3);

/// <summary>1セッション内の読取1件。EPC単位で後勝ち。</summary>
public sealed record ReadingEntry(string Epc, string? SearchKey, string? DeviceId, DateTimeOffset ReadAt, ReadLocation? Location = null);

public interface IReadingStore
{
    void UpsertBatch(Guid sessionId, IReadOnlyList<ReadingEntry> entries); // (session,epc)一致なら上書き（後勝ち）を一括
    void Upsert(Guid sessionId, ReadingEntry entry);                       // 便宜: UpsertBatch([entry]) へ委譲
    IReadOnlyList<ReadingEntry> List(Guid sessionId);
    int CountUnique(Guid sessionId);
}

public interface IProductCatalog
{
    /// <summary>検索キー(hex)からSKUを解決。未登録は null。</summary>
    string? ResolveSku(int tenantId, string searchKey);
}

/// <param name="ItemCount">Count of distinct SKU lines in the aggregated payload (not total units).</param>
public sealed record SnapshotRecord(Guid SessionId, int Version, bool IsFinal, Guid IdempotencyKey, int ItemCount, bool Success);

public interface ISnapshotStore
{
    int NextVersion(Guid sessionId); // セッション内で単調増加
    void Record(SnapshotRecord record);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string name, CancellationToken ct = default);
}

public sealed record WebhookRequest(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string Body);

public sealed record WebhookResult(bool Success, int StatusCode);

public interface IWebhookSender
{
    Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default);
}

public sealed record ReachabilityResult(int Expected, int Received)
{
    public bool IsMatch => Expected == Received;
    public int Missing => Expected - Received;
}

public interface IDeviceFeedback
{
    Task SendReachabilityAsync(Guid sessionId, ReachabilityResult result, CancellationToken ct = default);
}

public sealed record ProductRecord(
    int TenantId,
    string SearchKey,
    string Sku,
    string? ItemCode,
    string? Color,
    string? Size,
    string? Description);

public interface IProductWriteStore
{
    void Upsert(ProductRecord product); // (TenantId, SearchKey) で上書き
}

public interface IDestinationCatalog
{
    /// <summary>テナントの有効な配信先を DeliveryTarget として返す。</summary>
    IReadOnlyList<DeliveryTarget> GetActiveTargets(int tenantId);
}
