using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class OnboardingToResolutionTests
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
    public void RegisteredProduct_ResolvesFromScannedTagOfAnySerial()
    {
        // --- onboarding ---
        var catalog = new InMemoryProductCatalog();
        var registrar = new ProductRegistrar(catalog);
        var gtin = MakeGtin14("0000000000001");
        var key = registrar.Register(tenantId: 1, gtin14: gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        // --- 現場で読まれた実タグ(同一商品・任意シリアル) ---
        var tag = Convert.FromHexString(key);
        tag[11] = 0x2A;
        tag[10] = 0x13; // 適当なシリアルを下位に載せる
        var epcHex = Convert.ToHexString(tag);

        // --- 取込パイプラインのSKU化 ---
        var resolvedKey = Sgtin96.DeriveSearchKey(epcHex);
        Assert.Equal(key, resolvedKey);
        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, resolvedKey));
    }
}
