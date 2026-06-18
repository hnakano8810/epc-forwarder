# 詳細設計：端末（handy）フィードバック契約

対象: クラウド→端末／端末→クラウド の双方向フィードバック（基本設計2.3）。
原則: 端末へ返すのは **読取実績だけから生成できる情報**（SKU明細・総数・ロケ別明細・到達性検証）。誤出荷/期待明細突合は基幹連携前提のためスコープ外。

---

## 1. MQTTの前提（再掲）

MQTTはpub/sub（投げっぱなし）で**同期レスポンスを持たない**（QoSのACKは到達確認のみでデータを返さない）。したがってフィードバックは下りチャネルによる**非同期**となる。手段は2系統:

| チャネル | 実体 | 向き | 用途 |
|---|---|---|---|
| **プッシュ** | IoT Hub Cloud-to-Device (C2D)。端末は `devices/{id}/messages/devicebound/#` を subscribe | クラウド→端末 | 到達性不一致（再読取依頼）、未知タグ通知 |
| **プル** | HTTPクエリAPI（Functions HTTPトリガー） | 端末→クラウド（同期） | SKU明細・総数・ロケ別明細のオンデマンド取得 |

> MQTT 5 を使う場合は Response Topic + Correlation Data で相関可能だが、IoT Hub標準（MQTT 3.1.1）ではC2Dを用いる。

---

## 2. プッシュ：到達性検証フィードバック（C2D）

伝票の完了イベント受信 → settle猶予後にクラウドが受信ユニーク数と `expected_count` を突合 → **不一致時のみ**端末へ通知（基本設計2.1）。

```json
{
  "kind": "reachability",
  "session_id": "9c3a8f10-...",
  "business_key": "DN-2026-000123",
  "expected": 45,
  "received": 43,
  "status": "mismatch",
  "action": "re_read",
  "message": "通信で2件取りこぼしました。お手数ですが再度読み取ってください。"
}
```
- これは**システム到達性の問題**であり業務エラーではない旨をメッセージで明示。
- 一致時は `status:"ok"` を返すか、無通知（端末はプルで明細確認）。実装で確定（§5-1）。

### 2.1 未知タグ通知（任意）
```json
{ "kind": "unknown_tags", "session_id": "9c3a8f10-...", "count": 3 }
```
商品マスタ未登録EPCを検知した場合に件数を通知（data-model §6.4）。

---

## 3. プル：HTTPクエリAPI

端末が必要時に集約を取得して表示する。すべて `tenant` スコープで認可。

| メソッド/パス | 用途 | 主クエリ |
|---|---|---|
| `GET /api/sessions/{publicId}/summary` | 伝票/棚卸のSKU別数量＋総数 | data-model §6.1 |
| `GET /api/sessions/{publicId}/summary?groupBy=location` | 棚卸: ロケ別SKU明細 | data-model §6.2 |
| `GET /api/sessions/{publicId}/reconciliation` | 到達性検証結果（expected/received） | — |
| `GET /api/sessions/{publicId}/unknown` | 未知タグ一覧 | data-model §6.4 |

### 3.1 summary レスポンス例（伝票）
```json
{
  "session_id": "9c3a8f10-...",
  "type": "shipment",
  "total_quantity": 43,
  "items": [
    { "sku": "ITEM-AAA", "quantity": 2 },
    { "sku": "ITEM-BBB", "quantity": 1 }
  ],
  "unknown_count": 0,
  "as_of": "2026-06-18T12:34:56Z"
}
```

### 3.2 summary レスポンス例（棚卸・ロケ別）
```json
{
  "session_id": "9c3a8f10-...",
  "type": "inventory",
  "locations": [
    { "location": "A-01", "total_quantity": 1200,
      "items": [ { "sku": "ITEM-AAA", "quantity": 800 }, { "sku": "ITEM-BBB", "quantity": 400 } ] },
    { "location": "A-02", "total_quantity": 530, "items": [ ... ] }
  ],
  "as_of": "2026-06-18T12:34:56Z"
}
```

---

## 4. 認可

- 端末→クエリAPI の認証は、テナント/デバイス スコープのトークンを想定（PoCは Functions キー or 簡易APIキー、本番はAAD/デバイストークン）。
- API は常に `tenant_id` で絞り、他テナントのセッションへアクセス不可。

---

## 5. 未決事項

1. 到達性 **一致時** に明示的OK通知を出すか（端末UXに依存）。
2. プッシュ（C2D）と「完了イベントのHTTP同期応答」のどちらを一次手段にするか。完了イベントをHTTPで送る設計なら、その応答で到達性結果を即返せる（MQTTのみなら必ずC2D）。
3. 端末→APIの認証方式の確定（PoC: Functionsキー / 本番: AAD・デバイストークン）。
4. ロケ別明細のページング（ロケ数・SKU数が多い倉庫）。
