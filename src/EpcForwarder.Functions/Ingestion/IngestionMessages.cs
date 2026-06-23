using System.Text.Json.Serialization;

namespace EpcForwarder.Functions.Ingestion;

/// <summary>取込メッセージの判別用ヘッダ(kind だけ先読みする)。</summary>
public sealed class MessageKind
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}

/// <summary>読取バッチ(wire形式)。snake_case。EPC 配列を1メッセージで運ぶ。</summary>
public sealed class ReadsMessage
{
    [JsonPropertyName("tenant")] public int Tenant { get; set; }
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("business_key")] public string? BusinessKey { get; set; }
    [JsonPropertyName("session_type")] public string SessionType { get; set; } = "";
    [JsonPropertyName("resolve_sku")] public bool ResolveSku { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("epcs")] public List<ReadEntryDto> Epcs { get; set; } = [];
}

/// <summary>読取バッチ内の1件(per-read)。</summary>
public sealed class ReadEntryDto
{
    [JsonPropertyName("epc")] public string Epc { get; set; } = "";
    [JsonPropertyName("read_at")] public DateTimeOffset ReadAt { get; set; }
    [JsonPropertyName("location")] public LocationDto? Location { get; set; }
}

public sealed class LocationDto
{
    [JsonPropertyName("l1")] public string? L1 { get; set; }
    [JsonPropertyName("l2")] public string? L2 { get; set; }
    [JsonPropertyName("l3")] public string? L3 { get; set; }
}

/// <summary>完了イベント(wire形式)。</summary>
public sealed class CompleteMessage
{
    [JsonPropertyName("tenant")] public int Tenant { get; set; }
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("expected_count")] public int ExpectedCount { get; set; }
}
