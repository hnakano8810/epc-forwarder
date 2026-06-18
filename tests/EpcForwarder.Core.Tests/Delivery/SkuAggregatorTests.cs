using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class SkuAggregatorTests
{
    [Fact]
    public void Aggregate_CountsBySku_SortedByOrdinal()
    {
        var result = SkuAggregator.Aggregate(new[] { "ITEM-BBB", "ITEM-AAA", "ITEM-AAA" });

        Assert.Collection(result,
            i => { Assert.Equal("ITEM-AAA", i.Sku); Assert.Equal(2, i.Quantity); },
            i => { Assert.Equal("ITEM-BBB", i.Sku); Assert.Equal(1, i.Quantity); });
    }

    [Fact]
    public void Aggregate_Empty_ReturnsEmpty()
    {
        Assert.Empty(SkuAggregator.Aggregate(Array.Empty<string>()));
    }
}
