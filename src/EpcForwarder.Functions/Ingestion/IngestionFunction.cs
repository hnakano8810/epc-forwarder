using EpcForwarder.Core.Ingestion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace EpcForwarder.Functions.Ingestion;

/// <summary>
/// IoT Hub 内蔵 Event Hubs 互換エンドポイントからの取込。読取/完了をバッチで受け Dispatcher へ流す。
/// バインディング自体はローカル実行不可のためビルド検証のみ。
/// </summary>
public sealed class IngestionFunction(IngestionDispatcher dispatcher, ILogger<IngestionFunction> logger)
{
    [Function("Ingestion")]
    public async Task Run(
        [EventHubTrigger("%IoTHubEventHubName%", Connection = "IoTHubEventHubConnection", ConsumerGroup = "functions", IsBatched = true)] string[] messages,
        CancellationToken ct)
    {
        foreach (var raw in messages)
        {
            IIngestionCommand command;
            try
            {
                command = IngestionMessageParser.Parse(raw);
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                // PoC: 不正メッセージはログのみ(at-least-once 再処理での毒メッセージ防止)。
                // 全文/PIIを出さないよう先頭120文字に切り詰めたプレビューのみ記録。
                var preview = raw.Length > 120 ? raw[..120] + "…" : raw;
                logger.LogWarning(ex, "Skipping malformed ingestion message (preview={Preview}).", preview);
                continue;
            }

            switch (command)
            {
                case ReadBatchCommand reads:
                    dispatcher.IngestReads(reads);
                    break;
                case CompleteCommand complete:
                    var outcome = await dispatcher.CompleteAsync(complete, ct);
                    logger.LogInformation(
                        "Completion session={SessionId} expected={Expected} received={Received} delivered={Delivered}",
                        complete.SessionId, outcome.Reachability.Expected, outcome.Reachability.Received, outcome.Delivered);
                    break;
                default:
                    logger.LogWarning("Unhandled ingestion command type {Type}.", command.GetType().Name);
                    break;
            }
        }
    }
}
