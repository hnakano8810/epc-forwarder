// tests/EpcForwarder.Core.Tests/Sessions/ShipmentReconcilerTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class ShipmentReconcilerTests
{
    private static (ShipmentReconciler Sut, Guid Id, InMemoryReadingStore Readings, CapturingDeviceFeedback Fb)
        Build(int receivedCount)
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var fb = new CapturingDeviceFeedback();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));
        for (var i = 0; i < receivedCount; i++)
        {
            readings.Upsert(id, new ReadingEntry($"EPC{i}", $"K{i}", "devA", clock.UtcNow));
        }
        return (new ShipmentReconciler(sessions, readings, fb, clock), id, readings, fb);
    }

    [Fact]
    public async Task Complete_Match_NoFeedback()
    {
        var (sut, id, _, fb) = Build(receivedCount: 3);

        var result = await sut.CompleteAsync(id, expectedCount: 3);

        Assert.True(result.IsMatch);
        Assert.Empty(fb.Sent);
    }

    [Fact]
    public async Task Complete_Mismatch_SendsReReadFeedback()
    {
        var (sut, id, _, fb) = Build(receivedCount: 3);

        var result = await sut.CompleteAsync(id, expectedCount: 5);

        Assert.False(result.IsMatch);
        Assert.Equal(2, result.Missing);
        var sent = Assert.Single(fb.Sent);
        Assert.Equal(id, sent.SessionId);
        Assert.Equal(2, sent.Result.Missing);
    }
}
