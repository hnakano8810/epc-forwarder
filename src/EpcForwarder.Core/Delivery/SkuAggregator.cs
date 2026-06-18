namespace EpcForwarder.Core.Delivery;

public static class SkuAggregator
{
    public static IReadOnlyList<AggregateItem> Aggregate(IEnumerable<string> skus) =>
        skus.GroupBy(s => s)
            .Select(g => new AggregateItem(g.Key, g.Count()))
            .OrderBy(i => i.Sku, StringComparer.Ordinal)
            .ToList();
}
