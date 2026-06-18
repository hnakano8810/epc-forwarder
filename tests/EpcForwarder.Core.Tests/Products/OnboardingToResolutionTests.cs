using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Tests.Epc;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Products;

public class OnboardingToResolutionTests
{
    [Fact]
    public void RegisteredProduct_ResolvesFromScannedTagOfAnySerial()
    {
        // --- onboarding ---
        var catalog = new InMemoryProductCatalog();
        var registrar = new ProductRegistrar(catalog);
        var gtin = GtinTestData.Gtin14("0000000000001");
        var key = registrar.Register(tenantId: 1, gtin14: gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        // --- 現場で読まれた実タグ(同一商品・任意シリアル) ---
        // 38bitシリアル全体に値を載せて、マスクが全シリアルビットを確実に落とすことを検証する。
        var tag = Convert.FromHexString(key);
        tag[11] = 0x2A;
        tag[10] = 0x13;
        tag[9] = 0x44;
        tag[8] = 0x55;
        tag[7] = (byte)(tag[7] | 0x3F);
        var epcHex = Convert.ToHexString(tag);

        // --- 取込パイプラインのSKU化 ---
        var resolvedKey = Sgtin96.DeriveSearchKey(epcHex);
        Assert.Equal(key, resolvedKey);
        Assert.Equal("ITEM-AAA", catalog.ResolveSku(1, resolvedKey));
    }
}
