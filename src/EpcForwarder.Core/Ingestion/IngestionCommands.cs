using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Ingestion;

/// <summary>取込メッセージのドメイン表現(wire形式の JSON は Functions 層が本型へ変換する)。</summary>
public interface IIngestionCommand { }

/// <summary>バッチ内の読取1件(per-read)。read_at は後勝ち判定に使う。location は棚卸の別ロケ再読で後勝ち収束に使う。</summary>
public sealed record ReadEntry(string Epc, DateTimeOffset ReadAt, ReadLocation? Location);

/// <summary>読取バッチ(EPC 配列を1メッセージで取り込む)。未知セッションは本コマンドのメタデータで遅延生成する。</summary>
public sealed record ReadBatchCommand(
    int Tenant,
    Guid SessionId,
    string? BusinessKey,
    SessionType SessionType,
    bool ResolveSku,
    string? DeviceId,
    IReadOnlyList<ReadEntry> Reads) : IIngestionCommand;

/// <summary>伝票の完了イベント。到達性突合のトリガー。</summary>
public sealed record CompleteCommand(int Tenant, Guid SessionId, int ExpectedCount) : IIngestionCommand;

/// <summary>完了処理の結果(ホスト側のログ用)。</summary>
public sealed record CompletionOutcome(ReachabilityResult Reachability, bool Delivered, WebhookResult? Delivery);
