using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class HmacSignerTests
{
    [Fact]
    public void Sign_Format_IsSha256PrefixedHex()
    {
        var sig = HmacSigner.Sign("secret", "2026-06-18T00:00:00Z", "{}");

        Assert.StartsWith("sha256=", sig);
        var hex = sig["sha256=".Length..];
        Assert.Equal(64, hex.Length); // SHA-256 = 32バイト = 64 hex
        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    [Fact]
    public void Sign_IsDeterministic()
    {
        Assert.Equal(
            HmacSigner.Sign("k", "t", "body"),
            HmacSigner.Sign("k", "t", "body"));
    }

    [Theory]
    [InlineData("k2", "t", "body")]
    [InlineData("k", "t2", "body")]
    [InlineData("k", "t", "body2")]
    public void Sign_DiffersWhenAnyInputChanges(string secret, string ts, string body)
    {
        Assert.NotEqual(
            HmacSigner.Sign("k", "t", "body"),
            HmacSigner.Sign(secret, ts, body));
    }
}
