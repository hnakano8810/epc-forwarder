using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class ProductRegistrarTests
{
    private static string MakeGtin14(string body13)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = body13[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d;
        }
        return body13 + (10 - (sum % 10)) % 10;
    }

    [Fact]
    public void Register_StoresProduct_UnderComputedSearchKey()
    {
        var catalog = new InMemoryProductCatalog();
        var sut = new ProductRegistrar(catalog);
        var gtin = MakeGtin14("0000000000001");

        var key = sut.Register(tenantId: 1, gtin14: gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        // 返ったキーは Sgtin96Encoder と一致し、そのキーで SKU を解決できる
        Assert.Equal(Sgtin96Encoder.EncodeSearchKey(gtin, 7, 1), key);
        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, key));
    }
}
