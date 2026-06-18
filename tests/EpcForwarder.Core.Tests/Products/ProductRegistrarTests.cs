using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Tests.Epc;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class ProductRegistrarTests
{
    [Fact]
    public void Register_StoresProduct_UnderComputedSearchKey()
    {
        var catalog = new InMemoryProductCatalog();
        var sut = new ProductRegistrar(catalog);
        var gtin = GtinTestData.Gtin14("0000000000001");

        var key = sut.Register(tenantId: 1, gtin14: gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        // 返ったキーは Sgtin96Encoder と一致し、そのキーで SKU を解決できる
        Assert.Equal(Sgtin96Encoder.EncodeSearchKey(gtin, 7, 1), key);
        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, key));
    }
}
