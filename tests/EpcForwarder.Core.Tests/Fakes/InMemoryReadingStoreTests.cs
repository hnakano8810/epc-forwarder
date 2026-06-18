// tests/EpcForwarder.Core.Tests/Fakes/InMemoryReadingStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Fakes;

public class InMemoryReadingStoreTests
{
    [Fact]
    public void Upsert_SameEpc_LastWriteWins()
    {
        var store = new InMemoryReadingStore();
        var s = Guid.NewGuid();
        store.Upsert(s, new ReadingEntry("EPC1", "K1", "devA", DateTimeOffset.UnixEpoch));
        store.Upsert(s, new ReadingEntry("EPC1", "K1", "devB", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.Equal(1, store.CountUnique(s));
        Assert.Equal("devB", store.List(s).Single().DeviceId);
    }
}
