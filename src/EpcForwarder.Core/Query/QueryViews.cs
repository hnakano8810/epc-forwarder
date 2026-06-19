using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Core.Query;

/// <summary>端末向け集約ビュー(読取実績のみから生成。配信ペイロードと同じ解決ロジック)。</summary>
public sealed record SummaryItem(string Sku, int Quantity);

public sealed record SummaryView(
    Guid SessionId,
    string Type,
    int TotalQuantity,
    IReadOnlyList<SummaryItem> Items,
    int UnknownCount,
    DateTimeOffset AsOf);

public sealed record LocationGroup(ReadLocation Location, int TotalQuantity, IReadOnlyList<SummaryItem> Items);

public sealed record LocationSummaryView(
    Guid SessionId,
    string Type,
    IReadOnlyList<LocationGroup> Locations,
    DateTimeOffset AsOf);

/// <summary>到達性(expected は完了前は null)。</summary>
public sealed record ReconciliationView(Guid SessionId, int? Expected, int Received)
{
    public bool? IsMatch => Expected is null ? null : Expected == Received;
    /// <summary>不足数(Expected-Received)。負値は超過(余分に読取)を表す。Expected 未設定時は null。</summary>
    public int? Missing => Expected is null ? null : Expected - Received;
}

public sealed record UnknownView(Guid SessionId, int Count, IReadOnlyList<string> Epcs);
