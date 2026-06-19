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
            "read" => ToRead(JsonSerializer.Deserialize<ReadMessage>(json, Options)!),
            "complete" => ToComplete(JsonSerializer.Deserialize<CompleteMessage>(json, Options)!),
            _ => throw new FormatException($"Unknown ingestion message kind: '{head.Kind}'."),
        };
    }

    private static ReadCommand ToRead(ReadMessage m) => new(
        m.Tenant,
        m.SessionId,
        m.BusinessKey,
        Enum.Parse<SessionType>(m.SessionType, ignoreCase: true),
        m.ResolveSku,
        m.Epc,
        m.DeviceId,
        m.Location is null ? null : new ReadLocation(m.Location.L1, m.Location.L2, m.Location.L3),
        m.ReadAt);

    private static CompleteCommand ToComplete(CompleteMessage m) => new(m.Tenant, m.SessionId, m.ExpectedCount);
}
