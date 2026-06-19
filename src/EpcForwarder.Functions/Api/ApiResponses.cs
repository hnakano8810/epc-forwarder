using System.Text.Json.Serialization;
using EpcForwarder.Core.Query;

namespace EpcForwarder.Functions.Api;

public sealed class ItemDto
{
    [JsonPropertyName("sku")] public string Sku { get; set; } = "";
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
}

public sealed class LocationDto
{
    [JsonPropertyName("l1")] public string? L1 { get; set; }
    [JsonPropertyName("l2")] public string? L2 { get; set; }
    [JsonPropertyName("l3")] public string? L3 { get; set; }
}

public sealed class SummaryDto
{
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("total_quantity")] public int TotalQuantity { get; set; }
    [JsonPropertyName("items")] public List<ItemDto> Items { get; set; } = new();
    [JsonPropertyName("unknown_count")] public int UnknownCount { get; set; }
    [JsonPropertyName("as_of")] public DateTimeOffset AsOf { get; set; }
}

public sealed class LocationGroupDto
{
    [JsonPropertyName("location")] public LocationDto Location { get; set; } = new();
    [JsonPropertyName("total_quantity")] public int TotalQuantity { get; set; }
    [JsonPropertyName("items")] public List<ItemDto> Items { get; set; } = new();
}

public sealed class LocationSummaryDto
{
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("locations")] public List<LocationGroupDto> Locations { get; set; } = new();
    [JsonPropertyName("as_of")] public DateTimeOffset AsOf { get; set; }
}

public sealed class ReconciliationDto
{
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("expected")] public int? Expected { get; set; }
    [JsonPropertyName("received")] public int Received { get; set; }
    [JsonPropertyName("missing")] public int? Missing { get; set; }
    [JsonPropertyName("match")] public bool? Match { get; set; }
}

public sealed class UnknownDto
{
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("epcs")] public List<string> Epcs { get; set; } = new();
}

public sealed class InventoryResultDto
{
    [JsonPropertyName("delivered")] public bool Delivered { get; set; }
    [JsonPropertyName("status_code")] public int? StatusCode { get; set; }
}

/// <summary>Core ビュー → wire DTO への射影(純粋関数。単体テスト対象)。</summary>
public static class ApiResponses
{
    public static SummaryDto ToDto(SummaryView v) => new()
    {
        SessionId = v.SessionId,
        Type = v.Type,
        TotalQuantity = v.TotalQuantity,
        Items = v.Items.Select(i => new ItemDto { Sku = i.Sku, Quantity = i.Quantity }).ToList(),
        UnknownCount = v.UnknownCount,
        AsOf = v.AsOf,
    };

    public static LocationSummaryDto ToDto(LocationSummaryView v) => new()
    {
        SessionId = v.SessionId,
        Type = v.Type,
        Locations = v.Locations.Select(g => new LocationGroupDto
        {
            Location = new LocationDto { L1 = g.Location.L1, L2 = g.Location.L2, L3 = g.Location.L3 },
            TotalQuantity = g.TotalQuantity,
            Items = g.Items.Select(i => new ItemDto { Sku = i.Sku, Quantity = i.Quantity }).ToList(),
        }).ToList(),
        AsOf = v.AsOf,
    };

    public static ReconciliationDto ToDto(ReconciliationView v) => new()
    {
        SessionId = v.SessionId,
        Expected = v.Expected,
        Received = v.Received,
        Missing = v.Missing,
        Match = v.IsMatch,
    };

    public static UnknownDto ToDto(UnknownView v) => new()
    {
        SessionId = v.SessionId,
        Count = v.Count,
        Epcs = v.Epcs.ToList(),
    };
}
