# 詳細設計：Webhook 連携契約

対象: 外部システムへの送信仕様（基本設計5）。レガシー受信先が**改修なし**で受け取れることを目標とする。

---

## 1. 送信トリガーと種別

| 種別 | トリガー | `is_final` | セッション状態 |
|---|---|---|---|
| 仮確定スナップショット | 棚卸: 手動/定期（`destination.allow_provisional=1`時のみ） | `false` | `open` のまま |
| 確定スナップショット | 伝票: 完了＋到達性検証OK ／ 棚卸: 締め切り | `true` | `finalized→forwarded` |

- 転送はセッション単位（**ロケーション単位ではない**）。集約は確定/仮確定時にDB全件から1回生成（基本設計5.2）。
- 各送信は `snapshot` 行として記録（version単調増加・監査・再送）。

---

## 2. HTTP リクエスト

`POST {destination.url}`（メソッドは設定可）。

### 2.1 標準ヘッダー
| ヘッダー | 例 | 説明 |
|---|---|---|
| `Content-Type` | `application/json; charset=utf-8` | |
| `Idempotency-Key` | `f1e2...`(UUID) | `snapshot.idempotency_key`。受信側の重複排除キー |
| `X-EPCF-Tenant` | `acme` | テナントコード |
| `X-EPCF-Session` | `9c3a...`(UUID) | `session.public_id` |
| `X-EPCF-Snapshot-Version` | `3` | セッション内で単調増加 |
| `X-EPCF-Is-Final` | `true`/`false` | 最終版判定 |
| `X-EPCF-Timestamp` | `2026-06-18T12:34:56Z` | 署名のリプレイ防止に使用 |
| `X-EPCF-Signature` | `sha256=...` | HMAC署名（§4、有効時のみ） |

### 2.2 カスタム/認証ヘッダー
- `destination_header` の各行を付与。値は **Key Vault** から解決（DBは参照名のみ）。
- 例: `Authorization: Bearer <token>`, `X-API-KEY: <key>`（基本設計5.1）。

---

## 3. ペイロード（モード別）

共通エンベロープ + `items`。`payload_mode` は `destination` で決定。

### 3.1 aggregate（SKU＋数量）— 既定
```json
{
  "tenant": "acme",
  "session_id": "9c3a8f10-...",
  "business_key": "DN-2026-000123",
  "type": "shipment",
  "snapshot_version": 3,
  "is_final": true,
  "idempotency_key": "f1e2d3c4-...",
  "generated_at": "2026-06-18T12:34:56Z",
  "items": [
    { "sku": "ITEM-AAA", "quantity": 2 },
    { "sku": "ITEM-BBB", "quantity": 1 }
  ],
  "unknown_tags": { "count": 0, "epcs": [] }
}
```

### 3.2 raw（マスタ無し・EPCそのまま）
```json
{
  "tenant": "acme", "session_id": "9c3a8f10-...", "type": "inventory",
  "snapshot_version": 1, "is_final": true, "idempotency_key": "...",
  "generated_at": "2026-06-18T12:34:56Z",
  "items": [
    { "epc": "302DB42318A0038000001231", "location": "A-01", "read_at": "2026-06-18T12:30:01Z" },
    { "epc": "302DB42318A0038000009999", "location": "A-01", "read_at": "2026-06-18T12:30:02Z" }
  ]
}
```

### 3.3 detail（SKU＋シリアル）※将来
```json
{ "items": [ { "sku": "ITEM-AAA", "epc": "302DB42318A0038000001231", "location": "A-01" } ] }
```

- **未知タグ**（aggregate/detailで商品マスタ未登録）は本体に混ぜず `unknown_tags` に件数＋EPCを分離（data-model §6.4）。

---

## 4. HMAC 署名（`hmac_enabled=1`）

- `X-EPCF-Signature: sha256=<hex>` = `HMAC-SHA256(key, X-EPCF-Timestamp + "." + rawBody)`。
- `key` は Key Vault（`destination.hmac_secret_ref`）。
- 受信側は同じ計算で検証し、`X-EPCF-Timestamp` の鮮度（例: ±5分）でリプレイを排除。

---

## 5. 配信セマンティクス

- **at-least-once**: IoT Hub/EHトリガーの再処理で重複送信があり得る → `Idempotency-Key` で受信側が排除（基本設計5.4）。
- **置換（replace-by-snapshot）**: 受信側は同一 `session_id` について `snapshot_version` の**最大値で全置換**する。古いversionは無視。`is_final=true` 受領で確定。
  - `allow_provisional=0` の受信先には仮確定（`is_final=false`）を送らない。
- **成功判定**: HTTP `2xx`。それ以外/タイムアウトは失敗。
- **リトライ**: 指数バックオフ（例: 1s,4s,16s… 最大N回）。`delivery_attempt` に各試行を記録。
- **ポイズン**: 規定回数失敗で `snapshot.status='dead'`、Storage Queueへ退避、管理画面に失敗ログ表示。**再送**は新 `snapshot`（version採番）で再起票。

---

## 6. スロットリング

- **宛先（テナント）単位**のレート制御（`destination.rate_limit_rps`）。トークンバケット or 宛先別キューでアプリ層実装。
- `host.json` のグローバル設定は全テナント共通になるため**主手段にしない**（基本設計5.3）。

---

## 7. SSRF / 送信先ガード（基本設計5.1）

送信前に `destination.url` を検証:
- スキームは原則 `https`（`http` は明示許可時のみ）。
- 名前解決後のIPが **プライベート/リンクローカル/ループバック/メタデータ(`169.254.169.254`)** ならブロック。
- DNSリバインディング対策として、検証時と接続時の解決IPを固定（解決済みIPへ接続）するか、再検証する。
- 許可/拒否リスト（CIDR）をテナント/システムで設定可能に。

---

## 8. 未決事項

1. ペイロードのスキーマバージョニング（`schema_version` フィールド要否）。
2. 大規模 `raw` 連携時のページング/分割POST（数十万EPCをそのまま送る場合）。aggregateは集約で小さいが、rawは大きくなりうる。
3. ヘッダー名（`X-EPCF-*`）の最終確定とドキュメント公開（受信先実装者向け）。
