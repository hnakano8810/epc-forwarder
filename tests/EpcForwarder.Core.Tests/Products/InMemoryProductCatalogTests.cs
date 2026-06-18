using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class InMemoryProductCatalogTests
{
    [Fact]
    public void Upsert_ThenResolveSku_ReturnsSku()
    {
        var catalog = new InMemoryProductCatalog();
        catalog.Upsert(new ProductRecord(1, "302DB42318A0038000000000", "ITEM-AAA", "ST-100", "BLK", "M", null));

        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, "302DB42318A0038000000000"));
        Assert.Null(catalog.ResolveSku(1, "FFFFFFFFFFFFFFFFFFFFFFFF")); // 未登録
        Assert.Null(catalog.ResolveSku(2, "302DB42318A0038000000000")); // 別テナント
    }
}
