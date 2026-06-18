using EpcForwarder.Core.Sessions;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class SessionTests
{
    private static Session NewOpen() =>
        new(Guid.NewGuid(), tenantId: 1, SessionType.Shipment, businessKey: "DN-1",
            now: DateTimeOffset.UnixEpoch);

    [Fact]
    public void New_Session_IsOpen()
    {
        Assert.Equal(SessionStatus.Open, NewOpen().Status);
    }

    [Fact]
    public void Finalize_FromOpen_Succeeds()
    {
        var s = NewOpen();
        s.Finalize(DateTimeOffset.UnixEpoch);
        Assert.Equal(SessionStatus.Finalized, s.Status);
        Assert.NotNull(s.FinalizedAt);
    }

    [Fact]
    public void MarkForwarded_RequiresFinalized()
    {
        var s = NewOpen();
        Assert.Throws<InvalidOperationException>(() => s.MarkForwarded(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Finalize_Twice_Throws()
    {
        var s = NewOpen();
        s.Finalize(DateTimeOffset.UnixEpoch);
        Assert.Throws<InvalidOperationException>(() => s.Finalize(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void MarkForwarded_AfterFinalize_Succeeds()
    {
        var s = NewOpen();
        s.Finalize(DateTimeOffset.UnixEpoch);
        s.MarkForwarded(DateTimeOffset.UnixEpoch);
        Assert.Equal(SessionStatus.Forwarded, s.Status);
    }
}
