using System.Net;
using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class WebhookUrlGuardTests
{
    private sealed class FakeResolver(params string[] ips) : IHostResolver
    {
        public IReadOnlyList<IPAddress> Resolve(string host) =>
            ips.Select(IPAddress.Parse).ToList();
    }

    private static readonly WebhookUrlGuardOptions Strict = new(AllowHttp: false, AllowPrivateNetworks: false);

    [Fact]
    public void Validate_PublicHttps_Ok()
    {
        WebhookUrlGuard.Validate("https://api.example.com/hook", Strict, new FakeResolver("93.184.216.34"));
    }

    [Fact]
    public void Validate_Http_WhenNotAllowed_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebhookUrlGuard.Validate("http://api.example.com/hook", Strict, new FakeResolver("93.184.216.34")));
    }

    [Theory]
    [InlineData("169.254.169.254")] // メタデータ
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    public void Validate_PrivateOrMetadata_Throws(string ip)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebhookUrlGuard.Validate("https://internal.example.com/hook", Strict, new FakeResolver(ip)));
    }

    [Fact]
    public void Validate_PrivateAllowed_Ok()
    {
        var opt = new WebhookUrlGuardOptions(AllowHttp: true, AllowPrivateNetworks: true);
        WebhookUrlGuard.Validate("http://127.0.0.1:5000/hook", opt, new FakeResolver("127.0.0.1"));
    }

    [Fact]
    public void Validate_EmptyResolve_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebhookUrlGuard.Validate("https://api.example.com/hook", Strict, new FakeResolver()));
    }
}
