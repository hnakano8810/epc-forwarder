// tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs
using System.Collections.Concurrent;
using System.Net.Http;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Tests.Fakes;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _n;
    public Guid NewGuid() => new($"00000000-0000-0000-0000-{(++_n):D12}");
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<Guid, Session> _map = new();
    public Session? Get(Guid publicId) => _map.GetValueOrDefault(publicId);
    public void Save(Session session) => _map[session.PublicId] = session;
}

public sealed class InMemoryReadingStore : IReadingStore
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, ReadingEntry>> _map = new();

    public void Upsert(Guid sessionId, ReadingEntry entry)
    {
        var bag = _map.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, ReadingEntry>());
        bag[entry.Epc] = entry; // last-write-wins by EPC
    }

    public IReadOnlyList<ReadingEntry> List(Guid sessionId) =>
        _map.TryGetValue(sessionId, out var bag) ? bag.Values.ToList() : Array.Empty<ReadingEntry>();

    public int CountUnique(Guid sessionId) =>
        _map.TryGetValue(sessionId, out var bag) ? bag.Count : 0;
}

public sealed class InMemoryProductCatalog : IProductCatalog, IProductWriteStore
{
    private readonly Dictionary<(int, string), ProductRecord> _map = new();

    // 既存テスト互換: SKUのみ登録
    public void Add(int tenantId, string searchKey, string sku) =>
        _map[(tenantId, searchKey)] = new ProductRecord(tenantId, searchKey, sku, null, null, null, null);

    public void Upsert(ProductRecord product) =>
        _map[(product.TenantId, product.SearchKey)] = product;

    public string? ResolveSku(int tenantId, string searchKey) =>
        _map.TryGetValue((tenantId, searchKey), out var p) ? p.Sku : null;
}

public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly Dictionary<Guid, int> _versions = new();
    public List<SnapshotRecord> Records { get; } = new();
    public int NextVersion(Guid sessionId)
    {
        var v = _versions.GetValueOrDefault(sessionId) + 1;
        _versions[sessionId] = v;
        return v;
    }
    public void Record(SnapshotRecord record) => Records.Add(record);
}

public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _map = new();
    public void Add(string name, string value) => _map[name] = value;
    public Task<string?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_map.GetValueOrDefault(name));
}

public sealed class CapturingWebhookSender : IWebhookSender
{
    public WebhookRequest? Last { get; private set; }
    public int SendCount { get; private set; }
    public List<WebhookRequest> Requests { get; } = new();
    public WebhookResult Next { get; set; } = new(true, 200);

    public Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        Last = request;
        Requests.Add(request);
        SendCount++;
        return Task.FromResult(Next);
    }
}

public sealed class CapturingDeviceFeedback : IDeviceFeedback
{
    public List<(Guid SessionId, ReachabilityResult Result)> Sent { get; } = new();
    public Task SendReachabilityAsync(Guid sessionId, ReachabilityResult result, CancellationToken ct = default)
    {
        Sent.Add((sessionId, result));
        return Task.CompletedTask;
    }
}

public sealed class FakeDestinationCatalog : IDestinationCatalog
{
    private readonly Dictionary<int, List<DeliveryTarget>> _map = new();
    public void Add(int tenantId, DeliveryTarget target)
    {
        if (!_map.TryGetValue(tenantId, out var list)) { list = new(); _map[tenantId] = list; }
        list.Add(target);
    }
    public IReadOnlyList<DeliveryTarget> GetActiveTargets(int tenantId) =>
        _map.TryGetValue(tenantId, out var list) ? list : Array.Empty<DeliveryTarget>();
}

public sealed class SingleClientHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
