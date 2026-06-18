using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class Gtin14Tests
{
    [Theory]
    [InlineData("00036000291452", true)]   // UPC 036000291452 (check digit 2) を14桁化
    [InlineData("00036000291453", false)]  // チェックディジット不正
    public void HasValidCheckDigit_Works(string gtin, bool expected)
    {
        Assert.Equal(expected, Gtin14.HasValidCheckDigit(gtin));
    }

    [Fact]
    public void Parse_SplitsFields_ByCompanyPrefixLength()
    {
        // "00036000291452" を gcpLength=7 で分解
        //   indicator   = '0'                 (index0)
        //   companyPrefix = "0036000" (index1..7) = 36000
        //   GTIN itemRef  = "29145"   (index8..12)
        //   SGTIN itemReference = indicator + GTIN itemRef = "0" + "29145" = 29145
        var (gtin, indicator, cp, itemRef) = Gtin14.Parse("00036000291452", gcpLength: 7);

        Assert.Equal("00036000291452", gtin);
        Assert.Equal('0', indicator);
        Assert.Equal(36000UL, cp);
        Assert.Equal(29145UL, itemRef);
    }

    [Theory]
    [InlineData("3600029145", 7)]          // 14桁でない
    [InlineData("0003600029145X", 7)]      // 非数字
    [InlineData("00036000291453", 7)]      // チェックディジット不正
    public void Parse_Invalid_Throws(string gtin, int gcpLength)
    {
        Assert.Throws<ArgumentException>(() => Gtin14.Parse(gtin, gcpLength));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    public void Parse_BadGcpLength_Throws(int gcpLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Gtin14.Parse("00036000291452", gcpLength));
    }
}
