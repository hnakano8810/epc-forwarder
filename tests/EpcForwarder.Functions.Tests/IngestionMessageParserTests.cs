using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Functions.Ingestion;
using Xunit;

namespace EpcForwarder.Functions.Tests;

public class IngestionMessageParserTests
{
    [Fact]
    public void Parse_Reads_MapsSharedAndPerReadFields()
    {
        const string json = """
        {"kind":"reads","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001",
         "business_key":"DN-1","session_type":"shipment","resolve_sku":true,"device_id":"handy-07",
         "epcs":[
           {"epc":"302DB42318A0038000001231","read_at":"2026-06-18T12:30:01Z","location":{"l1":"TOKYO-DC","l2":"2F","l3":"A-01"}},
           {"epc":"302DB42318A0038000001232","read_at":"2026-06-18T12:30:02Z"}
         ]}
        """;

        var cmd = Assert.IsType<ReadBatchCommand>(IngestionMessageParser.Parse(json));
        Assert.Equal(1, cmd.Tenant);
        Assert.Equal(Guid.Parse("9c3a8f10-0000-0000-0000-000000000001"), cmd.SessionId);
        Assert.Equal(SessionType.Shipment, cmd.SessionType);
        Assert.True(cmd.ResolveSku);
        Assert.Equal("handy-07", cmd.DeviceId);
        Assert.Equal("DN-1", cmd.BusinessKey);

        Assert.Equal(2, cmd.Reads.Count);
        Assert.Equal("302DB42318A0038000001231", cmd.Reads[0].Epc);
        Assert.Equal(DateTimeOffset.Parse("2026-06-18T12:30:01Z"), cmd.Reads[0].ReadAt);
        Assert.Equal("TOKYO-DC", cmd.Reads[0].Location!.L1);
        Assert.Equal("2F", cmd.Reads[0].Location!.L2);
        Assert.Equal("A-01", cmd.Reads[0].Location!.L3);
        Assert.Equal("302DB42318A0038000001232", cmd.Reads[1].Epc);
        Assert.Null(cmd.Reads[1].Location);
    }

    [Fact]
    public void Parse_Reads_SingleEpc_IsBatchOfOne()
    {
        const string json = """
        {"kind":"reads","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001",
         "session_type":"inventory","resolve_sku":false,
         "epcs":[{"epc":"302DB42318A0038000001231","read_at":"2026-06-18T12:30:01Z"}]}
        """;

        var cmd = Assert.IsType<ReadBatchCommand>(IngestionMessageParser.Parse(json));
        Assert.Single(cmd.Reads);
        Assert.Equal(SessionType.Inventory, cmd.SessionType);
        Assert.False(cmd.ResolveSku);
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

    [Fact]
    public void Parse_RetiredSingleReadKind_IsUnknown_Throws()
    {
        // 単数 read は廃止。未知 kind として扱われ skip 対象(FormatException)になることを固定する。
        const string json = """
        {"kind":"read","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001",
         "session_type":"shipment","resolve_sku":true,"epc":"302DB42318A0038000001231","read_at":"2026-06-18T12:30:01Z"}
        """;
        Assert.Throws<FormatException>(() => IngestionMessageParser.Parse(json));
    }

    [Fact]
    public void Parse_Reads_BadSessionType_ThrowsFormatException()
    {
        const string json = """
        {"kind":"reads","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001",
         "session_type":"unknown_type","resolve_sku":false,
         "epcs":[{"epc":"302DB42318A0038000001231","read_at":"2026-06-18T12:30:01Z"}]}
        """;
        Assert.Throws<FormatException>(() => IngestionMessageParser.Parse(json));
    }
}
