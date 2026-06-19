using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Ingestion;

/// <summary>取込メッセージのドメイン表現(wire形式の JSON は Functions 層が本型へ変換する)。</summary>
public interface IIngestionCommand { }

/// <summary>読取1件。未知セッションは本コマンドのメタデータで遅延生成する。</summary>
public sealed record ReadCommand(
    int Tenant,
    Guid SessionId,
    string? BusinessKey,
    SessionType SessionType,
    bool ResolveSku,
    string Epc,
    string? DeviceId,
    ReadLocation? Location,
    DateTimeOffset ReadAt) : IIngestionCommand;

/// <summary>伝票の完了イベント。到達性突合のトリガー。</summary>
public sealed record CompleteCommand(int Tenant, Guid SessionId, int ExpectedCount) : IIngestionCommand;

/// <summary>完了処理の結果(ホスト側のログ用)。</summary>
public sealed record CompletionOutcome(ReachabilityResult Reachability, bool Delivered, WebhookResult? Delivery);
