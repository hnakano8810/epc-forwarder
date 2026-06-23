# 詳細設計：取込(Ingestion)契約

対象: ハンディ→IoT Hub→Functions の取込メッセージ(基本設計2.1/3)。IoT Hub 内蔵 Event Hubs 互換エンドポイントから `Ingestion` 関数がバッチ取得する。

## メッセージ種別(`kind` で判別)

### reads(読取バッチ・EPC配列を1メッセージで)
入出荷・棚卸とも本形式。EPC 1枚=1メッセージ(旧 `read`)は廃止。RFID 規模(伝票あたり数百枚、棚卸で数万〜数十万件)で IoT Hub のメッセージ数課金・スロットルと SQL 往復が破綻するため、端末が読取をバッファして配列で送る。
```json
{
  "kind": "reads",
  "tenant": 1,
  "session_id": "9c3a8f10-...",
  "business_key": "DN-2026-000123",
  "session_type": "shipment",
  "resolve_sku": true,
  "device_id": "handy-07",
  "epcs": [
    { "epc": "302DB42318A0038000001231", "read_at": "2026-06-18T12:30:01Z", "location": { "l1": "TOKYO-DC", "l2": "2F", "l3": "A-01" } },
    { "epc": "302DB42318A0038000001232", "read_at": "2026-06-18T12:30:01Z" }
  ]
}
```
- **shared(メッセージ共通)**: `tenant` / `session_id` / `business_key` / `session_type` / `resolve_sku` / `device_id`。
- **per-read(`epcs[]` 各要素)**: `epc` / `read_at`(後勝ち判定に必須) / `location`(任意。伝票では省略可、棚卸の別ロケ再読で後勝ち収束に使用)。
- **遅延セッション生成**: 未知 `session_id` を持つ最初のメッセージで session を生成(`tenant`/`session_type`/`business_key` を使用)。以後はメタデータを上書きしない。
- **バッチ内 dedup**: 同一 `epc` は後勝ち(最大 `read_at`)で1件に集約してから一括 UPSERT(`(session,epc)` 後勝ち)。
- `session_type` は `shipment` / `inventory`(大文字小文字非依存)。不正値はメッセージskip(下記)。

#### エッジ側の推奨(非規範。サーバはこれに依存せず正しく動作する)
- `complete` 送信前に未送信 read を **flush 推奨**(下記 settle 窓ギャップの緩和)。
- 1メッセージは **≤ 約2000件 かつ ≤256KB** でチャンク分割(棚卸の数万〜数十万件は複数メッセージ前提)。IoT Hub の課金は 4KB 単位なので、小さいメッセージ乱発を避け配列でまとめる。

### complete(伝票の完了イベント)
```json
{ "kind": "complete", "tenant": 1, "session_id": "9c3a8f10-...", "expected_count": 45 }
```
- 到達性突合(received = ユニーク読取数 vs expected_count)。
- **一致**: 有効な宛先(先頭)へ確定スナップショットを配信し forwarded。
- **不一致**: 端末へ再読取フィードバック(現状 NullDeviceFeedback の no-op)、配信しない。
- **冪等**: at-least-once 再送で既に forwarded のセッションは再配信せず成功扱い(`IngestionDispatcher.CompleteAsync`)。

## 不正メッセージの扱い
- `kind` 不明 / JSON 不正 / `session_type` 不正は `FormatException`/`JsonException` として **ログのみでskip**(at-least-once 再処理での毒メッセージ停滞防止)。`Ingestion` 関数が先頭120文字プレビュー付きで警告ログ。
- それ以外の例外(DB障害・配信例外など)は伝播し、EventHub のリトライに委ねる。

## PoC の制約・既知ギャップ
- **settle 猶予なし**: complete が reads を追い越すと過少 received で不一致になりうる。再突合/猶予は将来対応(基本設計2.1 のとおり数百ms〜数秒の猶予 or 再突合)。エッジの flush-before-complete 推奨はこれを緩和するが、サーバは順序非依存で正しく動く。
- **複数宛先のファンアウト未対応**: `IDestinationCatalog.GetActiveTargets` の先頭のみへ配信(ShipmentDeliverer がセッション単位で1回 forwarded にするため)。
- **棚卸の完了/仮確定は本経路ではない**: HTTP(③b-2)で起動。complete は伝票用。
- **C2D 宛先デバイスID**: `IDeviceFeedback` がまだ宛先デバイスを運んでいない(③b 以降)。
- **complete の tenant は信頼前提**: 宛先解決は complete メッセージの `tenant` で行い、セッション(reads で作成)の tenant と突合しない。偽装 complete が別テナントの宛先を引く余地がある。本PoCは信頼された端末経路前提で許容(本番はトークン/テナント検証を導入)。
- **重複 complete の戻り**: 既配信(forwarded)への再 complete は `Delivered=true` だが `Delivery=null`(新規送信なし)を返す冪等動作。
- **トリガーのローカル実行不可**: EventHub バインディングは実 IoT Hub 必要。本PoCはビルド検証＋Dispatcher/パーサ/SQL の単体・統合テストまで。
