// tests/EpcForwarder.Infrastructure.Tests/Adapters/RuntimeAdapterTests.cs
using System.Net;
using EpcForwarder.Infrastructure.Net;
using EpcForwarder.Infrastructure.Runtime;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests.Adapters;

public class RuntimeAdapterTests
{
    [Fact]
    public void SystemClock_ReturnsUtcNow_WithinTolerance()
    {
        var before = DateTimeOffset.UtcNow;
        var now = new SystemClock().UtcNow;
        var after = DateTimeOffset.UtcNow;
        Assert.InRange(now, before, after);
    }

    [Fact]
    public void GuidIdGenerator_ProducesDistinctNonEmptyGuids()
    {
        var g = new GuidIdGenerator();
        var a = g.NewGuid();
        var b = g.NewGuid();
        Assert.NotEqual(Guid.Empty, a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DnsHostResolver_ResolvesIpLiteral_ToSameAddress()
    {
        var ips = new DnsHostResolver().Resolve("127.0.0.1");
        Assert.Contains(IPAddress.Parse("127.0.0.1"), ips);
    }

    [Fact]
    public void DnsHostResolver_ResolvesLocalhost_ToLoopback()
    {
        var ips = new DnsHostResolver().Resolve("localhost");
        Assert.Contains(ips, IPAddress.IsLoopback);
    }
}
