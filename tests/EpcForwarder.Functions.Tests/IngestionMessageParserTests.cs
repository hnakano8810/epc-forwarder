using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Functions.Ingestion;
using Xunit;

namespace EpcForwarder.Functions.Tests;

public class IngestionMessageParserTests
{
    [Fact]
    public void Parse_Read_MapsAllFields()
    {
        const string json = """
        {"kind":"read","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001",
         "business_key":"DN-1","session_type":"shipment","resolve_sku":true,
         "epc":"302DB42318A0038000001231","device_id":"handy-07",
         "location":{"l1":"TOKYO-DC","l2":"2F","l3":"A-01"},"read_at":"2026-06-18T12:30:01Z"}
        """;

        var cmd = Assert.IsType<ReadCommand>(IngestionMessageParser.Parse(json));
        Assert.Equal(1, cmd.Tenant);
        Assert.Equal(Guid.Parse("9c3a8f10-0000-0000-0000-000000000001"), cmd.SessionId);
        Assert.Equal(SessionType.Shipment, cmd.SessionType);
        Assert.True(cmd.ResolveSku);
        Assert.Equal("302DB42318A0038000001231", cmd.Epc);
        Assert.Equal("handy-07", cmd.DeviceId);
        Assert.Equal("TOKYO-DC", cmd.Location!.L1);
        Assert.Equal("A-01", cmd.Location!.L3);
    }

    [Fact]
    public void Parse_Complete_MapsFields()
    {
        const string json = """
        {"kind":"complete","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001","expected_count":45}
        """;

        var cmd = Assert.IsType<CompleteCommand>(IngestionMessageParser.Parse(json));
        Assert.Equal(45, cmd.ExpectedCount);
    }

    [Fact]
    public void Parse_UnknownKind_Throws()
    {
        Assert.Throws<FormatException>(() => IngestionMessageParser.Parse("""{"kind":"bogus"}"""));
    }
}
