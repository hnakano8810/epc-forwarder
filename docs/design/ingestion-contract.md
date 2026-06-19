# 詳細設計：取込(Ingestion)契約

対象: ハンディ→IoT Hub→Functions の取込メッセージ(基本設計2.1/3)。IoT Hub 内蔵 Event Hubs 互換エンドポイントから `Ingestion` 関数がバッチ取得する。

## メッセージ種別(`kind` で判別)

### read(読取1件・ストリーミング)
```json
{
  "kind": "read",
  "tenant": 1,
  "session_id": "9c3a8f10-...",
  "business_key": "DN-2026-000123",
  "session_type": "shipment",
  "resolve_sku": true,
  "epc": "302DB42318A0038000001231",
  "device_id": "handy-07",
  "location": { "l1": "TOKYO-DC", "l2": "2F", "l3": "A-01" },
  "read_at": "2026-06-18T12:30:01Z"
}
```
- **遅延セッション生成**: 未知 `session_id` の最初の read で session を生成(`tenant`/`session_type`/`business_key` を使用)。以後の read はメタデータを上書きしない。
- `location` は任意(伝票では省略可、棚卸のロケ別集計で使用)。
- `session_type` は `shipment` / `inventory`(大文字小文字非依存)。不正値はメッセージskip(下記)。

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
- **settle 猶予なし**: complete が read を追い越すと過少 received で不一致になりうる。再突合/猶予は将来対応(基本設計2.1 のとおり数百ms〜数秒の猶予 or 再突合)。
- **複数宛先のファンアウト未対応**: `IDestinationCatalog.GetActiveTargets` の先頭のみへ配信(ShipmentDeliverer がセッション単位で1回 forwarded にするため)。
- **棚卸の完了/仮確定は本経路ではない**: HTTP(③b-2)で起動。complete は伝票用。
- **C2D 宛先デバイスID**: `IDeviceFeedback` がまだ宛先デバイスを運んでいない(③b 以降)。
- **complete の tenant は信頼前提**: 宛先解決は complete メッセージの `tenant` で行い、セッション(read で作成)の tenant と突合しない。偽装 complete が別テナントの宛先を引く余地がある。本PoCは信頼された端末経路前提で許容(本番はトークン/テナント検証を導入)。
- **重複 complete の戻り**: 既配信(forwarded)への再 complete は `Delivered=true` だが `Delivery=null`(新規送信なし)を返す冪等動作。
- **トリガーのローカル実行不可**: EventHub バインディングは実 IoT Hub 必要。本PoCはビルド検証＋Dispatcher/パーサ/SQL の単体・統合テストまで。
