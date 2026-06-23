using System.Text.Json;
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Functions.Ingestion;

/// <summary>取込 JSON 1件を Core のコマンドへ変換する。未知 kind / 不正は例外。</summary>
public static class IngestionMessageParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IIngestionCommand Parse(string json)
    {
        var head = JsonSerializer.Deserialize<MessageKind>(json, Options)
            ?? throw new FormatException("Empty ingestion message.");

        return head.Kind switch
        {
            "reads" => ToReads(JsonSerializer.Deserialize<ReadsMessage>(json, Options)!),
            "complete" => ToComplete(JsonSerializer.Deserialize<CompleteMessage>(json, Options)!),
            _ => throw new FormatException($"Unknown ingestion message kind: '{head.Kind}'."),
        };
    }

    private static ReadBatchCommand ToReads(ReadsMessage m)
    {
        if (!Enum.TryParse<SessionType>(m.SessionType, ignoreCase: true, out var sessionType))
        {
            throw new FormatException($"Unknown session_type: '{m.SessionType}'.");
        }

        var reads = m.Epcs
            .Select(e => new ReadEntry(
                e.Epc,
                e.ReadAt,
                e.Location is null ? null : new ReadLocation(e.Location.L1, e.Location.L2, e.Location.L3)))
            .ToList();

        return new ReadBatchCommand(
            m.Tenant,
            m.SessionId,
            m.BusinessKey,
            sessionType,
            m.ResolveSku,
            m.DeviceId,
            reads);
    }

    private static CompleteCommand ToComplete(CompleteMessage m) => new(m.Tenant, m.SessionId, m.ExpectedCount);
}
