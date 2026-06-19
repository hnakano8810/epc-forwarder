using System.Text.Json;
using EpcForwarder.Core.Query;
using EpcForwarder.Functions.Api;
using Xunit;

namespace EpcForwarder.Functions.Tests.Api;

public class ApiResponsesTests
{
    [Fact]
    public void ToDto_Summary_SerializesSnakeCase()
    {
        var id = Guid.Parse("9c3a8f10-0000-0000-0000-000000000001");
        var view = new SummaryView(id, "shipment", 2, new[] { new SummaryItem("ITEM-AAA", 2) }, 1, DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(ApiResponses.ToDto(view));

        Assert.Contains("\"total_quantity\":2", json);
        Assert.Contains("\"unknown_count\":1", json);
        Assert.Contains("\"sku\":\"ITEM-AAA\"", json);
        Assert.Contains("\"as_of\"", json);
    }

    [Fact]
    public void ToDto_Reconciliation_MapsMatchAndMissing()
    {
        var id = Guid.NewGuid();
        var dto = ApiResponses.ToDto(new ReconciliationView(id, Expected: 5, Received: 3));

        Assert.Equal(5, dto.Expected);
        Assert.Equal(3, dto.Received);
        Assert.Equal(2, dto.Missing);
        Assert.False(dto.Match);
    }

    [Fact]
    public void ToDto_LocationSummary_MapsNestedLocation()
    {
        var id = Guid.NewGuid();
        var view = new LocationSummaryView(id, "inventory",
            new[] { new LocationGroup(new EpcForwarder.Core.Abstractions.ReadLocation("DC", "2F", "A-01"), 1, new[] { new SummaryItem("ITEM-AAA", 1) }) },
            DateTimeOffset.UnixEpoch);

        var dto = ApiResponses.ToDto(view);
        var grp = Assert.Single(dto.Locations);
        Assert.Equal("A-01", grp.Location.L3);
        Assert.Equal(1, grp.TotalQuantity);
        Assert.Equal("ITEM-AAA", Assert.Single(grp.Items).Sku);
    }
}
