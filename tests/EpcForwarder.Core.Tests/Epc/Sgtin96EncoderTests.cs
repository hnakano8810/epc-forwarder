using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class Sgtin96EncoderTests
{
    // 13桁(インジケータ+12桁)からチェックディジットを付けて有効なGTIN-14を作る
    private static string MakeGtin14(string body13)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            var d = body13[i] - '0';
            sum += ((12 - i) % 2 == 0) ? d * 3 : d;
        }
        var check = (10 - (sum % 10)) % 10;
        return body13 + check;
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Encode_AllPartitions_MaskConsistent_HeaderAndSerialZero(int gcpLength)
    {
        var gtin = MakeGtin14("0000000000001"); // 小さい値: 全partitionでフィールド幅に収まる
        var key = Sgtin96Encoder.EncodeSearchKey(gtin, gcpLength, filter: 1);
        var keyBytes = Convert.FromHexString(key);

        // ヘッダは 0x30
        Assert.Equal(0x30, keyBytes[0]);
        // シリアル(下位38bit)は0: 末尾4バイト=0、5バイト目(index7)の下位6bit=0
        Assert.Equal(0, keyBytes[11]);
        Assert.Equal(0, keyBytes[10]);
        Assert.Equal(0, keyBytes[9]);
        Assert.Equal(0, keyBytes[8]);
        Assert.Equal(0, keyBytes[7] & 0x3F);

        // マスク整合性: 任意シリアルを載せても EpcKey.Derive で同じキーに戻る
        var epc = (byte[])keyBytes.Clone();
        epc[11] = 0x99; epc[10] = 0x88; epc[9] = 0x77; epc[8] = 0x66;
        epc[7] = (byte)(epc[7] | 0x3F);
        var derived = EpcKey.Derive(epc, EpcKey.Sgtin96Mask);
        Assert.Equal(key, Convert.ToHexString(derived));
    }

    [Fact]
    public void Encode_DifferentGcpLength_ProducesDifferentKey()
    {
        var gtin = MakeGtin14("0000000000001");
        var k7 = Sgtin96Encoder.EncodeSearchKey(gtin, 7, filter: 1);
        var k8 = Sgtin96Encoder.EncodeSearchKey(gtin, 8, filter: 1);
        Assert.NotEqual(k7, k8); // partition が異なる
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-1)]
    public void Encode_BadFilter_Throws(int filter)
    {
        var gtin = MakeGtin14("0000000000001");
        Assert.Throws<ArgumentOutOfRangeException>(() => Sgtin96Encoder.EncodeSearchKey(gtin, 7, filter));
    }

    [Fact]
    public void Encode_BadGcpLength_Throws()
    {
        var gtin = MakeGtin14("0000000000001");
        Assert.Throws<ArgumentOutOfRangeException>(() => Sgtin96Encoder.EncodeSearchKey(gtin, 5, filter: 1));
    }
}
