using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class Sgtin96EncoderTests
{
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
        var gtin = GtinTestData.Gtin14("0000000000001"); // 小さい値: 全partitionでフィールド幅に収まる
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
        var gtin = GtinTestData.Gtin14("0000000000001");
        var k7 = Sgtin96Encoder.EncodeSearchKey(gtin, 7, filter: 1);
        var k8 = Sgtin96Encoder.EncodeSearchKey(gtin, 8, filter: 1);
        Assert.NotEqual(k7, k8); // partition が異なる
    }

    [Theory]
    [InlineData(8)]
    [InlineData(-1)]
    public void Encode_BadFilter_Throws(int filter)
    {
        var gtin = GtinTestData.Gtin14("0000000000001");
        Assert.Throws<ArgumentOutOfRangeException>(() => Sgtin96Encoder.EncodeSearchKey(gtin, 7, filter));
    }

    [Fact]
    public void Encode_BadGcpLength_Throws()
    {
        var gtin = GtinTestData.Gtin14("0000000000001");
        Assert.Throws<ArgumentOutOfRangeException>(() => Sgtin96Encoder.EncodeSearchKey(gtin, 5, filter: 1));
    }

    [Fact]
    public void Encode_NearMaxCompanyPrefix_GcpLength12_EncodesWithoutFalseOverflow()
    {
        // gcpLength=12 → partition 0, CpBits=40。12桁CP最大 999,999,999,999 < 2^40。
        // つまり境界直下の有効CPは誤検出なくエンコードできるべき。
        var gtin = GtinTestData.Gtin14("0" + "999999999999"); // indicator 0 + 12桁CP(全9)
        var key = Sgtin96Encoder.EncodeSearchKey(gtin, gcpLength: 12, filter: 1);

        Assert.Equal(24, key.Length); // 24 hex桁
        var keyBytes = Convert.FromHexString(key);

        // ヘッダ 0x30 ＋ シリアル(下位38bit)=0
        Assert.Equal(0x30, keyBytes[0]);
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
}
