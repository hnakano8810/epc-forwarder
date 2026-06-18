// tests/EpcForwarder.Core.Tests/Sessions/SessionRehydrateTests.cs
using EpcForwarder.Core.Sessions;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class SessionRehydrateTests
{
    [Fact]
    public void Rehydrate_RestoresAllFields_WithoutTransitionChecks()
    {
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UnixEpoch;
        var finalized = created.AddMinutes(1);

        var s = Session.Rehydrate(
            publicId: id, tenantId: 7, type: SessionType.Inventory, businessKey: "CAMP-9",
            status: SessionStatus.Forwarded, expectedCount: 42,
            createdAt: created, lastEventAt: finalized, finalizedAt: finalized, forwardedAt: finalized);

        Assert.Equal(id, s.PublicId);
        Assert.Equal(7, s.TenantId);
        Assert.Equal(SessionType.Inventory, s.Type);
        Assert.Equal("CAMP-9", s.BusinessKey);
        Assert.Equal(SessionStatus.Forwarded, s.Status); // 遷移を経ずに直接 Forwarded
        Assert.Equal(42, s.ExpectedCount);
        Assert.Equal(finalized, s.FinalizedAt);
        Assert.Equal(finalized, s.ForwardedAt);
    }
}
