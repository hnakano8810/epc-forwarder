// tests/EpcForwarder.Infrastructure.Tests/Adapters/CachingSecretStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Secrets;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests.Adapters;

public class CachingSecretStoreTests
{
    private sealed class CountingInner : ISecretStore
    {
        public int Calls { get; private set; }
        public string? Value { get; set; } = "v1";
        public Task<string?> GetAsync(string name, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Value);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task GetAsync_CachesWithinTtl_InnerCalledOnce()
    {
        var inner = new CountingInner();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sut = new CachingSecretStore(inner, clock, TimeSpan.FromMinutes(5));

        var a = await sut.GetAsync("s");
        var b = await sut.GetAsync("s");

        Assert.Equal("v1", a);
        Assert.Equal("v1", b);
        Assert.Equal(1, inner.Calls); // 2回目はキャッシュ
    }

    [Fact]
    public async Task GetAsync_RefetchesAfterTtlExpiry()
    {
        var inner = new CountingInner();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sut = new CachingSecretStore(inner, clock, TimeSpan.FromMinutes(5));

        await sut.GetAsync("s");
        clock.UtcNow = clock.UtcNow.AddMinutes(6); // TTL超過
        inner.Value = "v2";
        var after = await sut.GetAsync("s");

        Assert.Equal("v2", after);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task GetAsync_AtExactlyTtl_Refetches()
    {
        var inner = new CountingInner();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sut = new CachingSecretStore(inner, clock, TimeSpan.FromMinutes(5));

        await sut.GetAsync("s");
        clock.UtcNow = clock.UtcNow.AddMinutes(5); // exactly at expiry
        await sut.GetAsync("s");

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task GetAsync_DoesNotCacheNull()
    {
        var inner = new CountingInner { Value = null };
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sut = new CachingSecretStore(inner, clock, TimeSpan.FromMinutes(5));

        await sut.GetAsync("s");
        await sut.GetAsync("s");

        Assert.Equal(2, inner.Calls); // null は毎回再取得
    }
}
