using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class Sgtin96Tests
{
    [Theory]
    [InlineData("302DB42318A0038000001231", "302DB42318A0038000000000")]
    [InlineData("302db42318a0038000009999", "302DB42318A0038000000000")] // 小文字入力も許容
    public void DeriveSearchKey_ZeroesSerial(string epcHex, string expected)
    {
        Assert.Equal(expected, Sgtin96.DeriveSearchKey(epcHex));
    }

    [Theory]
    [InlineData("302DB42318A00380000012")]   // 22桁（短い）
    [InlineData("302DB42318A0038000001231AA")] // 26桁（長い）
    [InlineData("ZZZZB42318A0038000001231")]  // 非hex
    public void DeriveSearchKey_InvalidEpc_Throws(string epcHex)
    {
        Assert.Throws<ArgumentException>(() => Sgtin96.DeriveSearchKey(epcHex));
    }
}
