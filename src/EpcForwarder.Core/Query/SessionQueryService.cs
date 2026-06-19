using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Query;

/// <summary>
/// 端末向け読取モデル。tenant スコープで session を解決し、読取実績から集約ビューを生成する。
/// 解決ロジックは SnapshotPublisher と同一(SearchKey null またはマスタ未登録は未知タグ)。
/// session 不在 or tenant 不一致は null を返す(呼び出し側で 404)。
/// 注: GetSummary と GetUnknown はそれぞれ readings.List を1回呼ぶ。両方必要なリクエストでは2回走る(PoCでは許容)。
/// </summary>
public sealed class SessionQueryService(
    ISessionStore sessions,
    IReadingStore readings,
    IProductCatalog products,
    IClock clock)
{
    public SummaryView? GetSummary(int tenantId, Guid sessionId)
    {
        var session = ResolveSession(tenantId, sessionId);
        if (session is null)
        {
            return null;
        }

        var (resolved, unknown) = ResolveSkus(tenantId, readings.List(sessionId));
        var items = SkuAggregator.Aggregate(resolved).Select(a => new SummaryItem(a.Sku, a.Quantity)).ToList();
        return new SummaryView(
            sessionId,
            session.Type.ToString().ToLowerInvariant(),
            TotalQuantity: resolved.Count,
            Items: items,
            UnknownCount: unknown.Count,
            AsOf: clock.UtcNow);
    }

    public UnknownView? GetUnknown(int tenantId, Guid sessionId)
    {
        var session = ResolveSession(tenantId, sessionId);
        if (session is null)
        {
            return null;
        }

        var (_, unknown) = ResolveSkus(tenantId, readings.List(sessionId));
        return new UnknownView(sessionId, unknown.Count, unknown);
    }

    public ReconciliationView? GetReconciliation(int tenantId, Guid sessionId)
    {
        var session = ResolveSession(tenantId, sessionId);
        if (session is null)
        {
            return null;
        }

        return new ReconciliationView(sessionId, session.ExpectedCount, readings.CountUnique(sessionId));
    }

    public LocationSummaryView? GetLocationSummary(int tenantId, Guid sessionId)
    {
        var session = ResolveSession(tenantId, sessionId);
        if (session is null)
        {
            return null;
        }

        var groups = new List<LocationGroup>();
        // ロケ単位に分け、各ロケ内で SKU 集約(未知タグはロケ別ビューには含めない=本体集約と同方針)。
        foreach (var grp in readings.List(sessionId).GroupBy(r => r.Location ?? new ReadLocation(null, null, null)))
        {
            var resolved = new List<string>();
            foreach (var entry in grp)
            {
                var sku = TryResolveSku(tenantId, entry);
                if (sku is not null)
                {
                    resolved.Add(sku);
                }
            }

            var items = SkuAggregator.Aggregate(resolved).Select(a => new SummaryItem(a.Sku, a.Quantity)).ToList();
            groups.Add(new LocationGroup(grp.Key, resolved.Count, items));
        }

        return new LocationSummaryView(
            sessionId,
            session.Type.ToString().ToLowerInvariant(),
            groups,
            clock.UtcNow);
    }

    private Session? ResolveSession(int tenantId, Guid sessionId)
    {
        var session = sessions.Get(sessionId);
        return session is not null && session.TenantId == tenantId ? session : null;
    }

    private (List<string> Resolved, List<string> Unknown) ResolveSkus(int tenantId, IReadOnlyList<ReadingEntry> entries)
    {
        var resolved = new List<string>();
        var unknown = new List<string>();
        foreach (var entry in entries)
        {
            var sku = TryResolveSku(tenantId, entry);
            if (sku is null)
            {
                unknown.Add(entry.Epc);
            }
            else
            {
                resolved.Add(sku);
            }
        }

        return (resolved, unknown);
    }

    private string? TryResolveSku(int tenantId, ReadingEntry entry) =>
        entry.SearchKey is null ? null : products.ResolveSku(tenantId, entry.SearchKey);
}
