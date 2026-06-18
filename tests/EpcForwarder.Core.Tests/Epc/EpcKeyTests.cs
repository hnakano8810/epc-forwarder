using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class EpcKeyTests
{
    [Theory]
    // docs/design/epc-mask.md §4 テストベクタ
    [InlineData("302DB42318A0038000001231", "302DB42318A0038000000000")] // #1 基本ケース
    [InlineData("302DB42318A0038000009999", "302DB42318A0038000000000")] // #2 同一商品・別シリアル → 同一キー
    public void Derive_Sgtin96_ZeroesSerial(string epcHex, string expectedKeyHex)
    {
        var epc = Convert.FromHexString(epcHex);

        var key = EpcKey.Derive(epc, EpcKey.Sgtin96Mask);

        Assert.Equal(expectedKeyHex, Convert.ToHexString(key));
    }

    [Fact]
    public void Derive_LengthMismatch_Throws()
    {
        var epc = new byte[11]; // mask は12バイト

        Assert.Throws<ArgumentException>(() => EpcKey.Derive(epc, EpcKey.Sgtin96Mask));
    }
}
