# 詳細設計：データモデル (Azure SQL Database)

対象: EPC Forwarder のデータ永続化層（基本設計 4.1 / 6 に対応）。
方針: 本システムはシステム・オブ・レコードにならない。保持するのは **セッションが転送を終えるまでの一時状態** と、**テナント設定・マスタ系**のみ。

---

## 1. 全体像

| 区分 | テーブル | 寿命 | 役割 |
|---|---|---|---|
| マスタ | `tenant` | 恒久 | テナント |
| マスタ | `mask` | 恒久（任意） | スキーム別マスク定義（v1はSGTIN-96の1本。rawモードでは不要） |
| マスタ | `product` | 恒久（任意） | 検索キー→SKU の商品マスタ（rawモードでは不要） |
| 設定 | `destination` | 恒久 | Webhook宛先・認証・モード |
| 設定 | `destination_header` | 恒久 | 宛先ごとの任意HTTPヘッダー |
| セッション | `session` | 一時（転送完了後TTL） | 伝票/棚卸セッション |
| セッション | `reading` | 一時 | 読取実績（後勝ち・除外フラグ） |
| セッション | `snapshot` | 一時+監査 | 転送（仮確定/確定）の記録・再送 |
| セッション | `delivery_attempt` | 一時+監査 | 個々の送信試行ログ |

全テーブルに `tenant_id` を持たせ、すべてのクエリでテナントを絞る。Azure SQL の **Row-Level Security (RLS)** を多層防御として併用可能（後述 §7）。

---

## 2. 識別子・型の方針

- **主キー**: `BIGINT IDENTITY`（クラスタ化キーをほぼ単調増加にして挿入時のページ分割を抑制）。外部公開IDが要る場合は別途 `public_id UNIQUEIDENTIFIER`。
- **EPC / 検索キー**: `VARBINARY(32)`。SGTIN-96は12バイト、SGTIN-198は25バイト。余裕を見て32バイト幅とする。二進のまま持つことでマスクのAND演算・等価検索・インデックスが正確かつ高速。
- **時刻**: `DATETIME2(3)` UTC。
- **テナントID**: `INT`（`tenant.tenant_id`）。

---

## 3. マスタ系

### 3.1 tenant
```sql
CREATE TABLE dbo.tenant (
    tenant_id   INT IDENTITY PRIMARY KEY,
    code        NVARCHAR(64)  NOT NULL UNIQUE,   -- 外部識別用テナントコード
    name        NVARCHAR(200) NOT NULL,
    status      VARCHAR(16)   NOT NULL DEFAULT 'active', -- active/suspended
    created_at  DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME()
);
```

### 3.2 mask（スキーム別マスク定義）
```sql
CREATE TABLE dbo.mask (
    mask_id      INT IDENTITY PRIMARY KEY,
    tenant_id    INT          NOT NULL REFERENCES dbo.tenant(tenant_id),
    scheme       VARCHAR(32)  NOT NULL,           -- 'SGTIN-96' 等
    mask_value   VARBINARY(32) NOT NULL,          -- 例: 0xFFFFFFFFFFFFFFC000000000 (下位38bit=0)
    header_match VARBINARY(2)  NULL,              -- 将来: ヘッダービットによるルーティング (例: 0x30)
    is_active    BIT          NOT NULL DEFAULT 1,
    created_at   DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
```
- v1: テナントあたり SGTIN-96 の1行のみを想定。複数マスク対応は将来（基本設計3.3）。マスク値の厳密仕様は `epc-mask.md` 参照。
- **rawモードではマスク不要**: SKU解決を行わず生EPCをそのまま連携する宛先（§4.1 `payload_mode='raw'`）では、`mask`・`product` を持たなくてよい。

### 3.3 product（商品マスタ：検索キー→SKU）
```sql
CREATE TABLE dbo.product (
    tenant_id    INT           NOT NULL REFERENCES dbo.tenant(tenant_id),
    search_key   VARBINARY(32) NOT NULL,          -- EPC & Mask の結果（シリアル0クリア済み）
    sku          NVARCHAR(64)  NOT NULL,
    description  NVARCHAR(200) NULL,
    updated_at   DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_product PRIMARY KEY (tenant_id, search_key)  -- O(1)検索の核
);
```
- 登録時に「既知GTIN/独自コード → 検索キー」へ変換して投入する（基本設計の onboarding。標準SGTIN-96でもGTIN→検索キーのエンコーダが必要、`epc-mask.md` 参照）。
- `(tenant_id, search_key)` の複合PKで、受信EPCをマスクした値から一発でSKU特定。
- **商品マスタは任意**: 「何らかの単位で読んでシリアル(EPC)だけを連携したい」顧客向けに、`product` を持たず SKU解決をスキップする **rawモード** を許容する（§4.1）。この場合 `reading.search_key` はNULL。

---

## 4. 宛先（Webhook）設定

### 4.1 destination
```sql
CREATE TABLE dbo.destination (
    destination_id   INT IDENTITY PRIMARY KEY,
    tenant_id        INT          NOT NULL REFERENCES dbo.tenant(tenant_id),
    name             NVARCHAR(200) NOT NULL,
    url              NVARCHAR(2048) NOT NULL,
    http_method      VARCHAR(8)   NOT NULL DEFAULT 'POST',
    payload_mode     VARCHAR(16)  NOT NULL DEFAULT 'aggregate', -- aggregate(SKU+数量) / detail(SKU+シリアル,将来) / raw(マスタ無し・EPCそのまま)
    allow_provisional BIT         NOT NULL DEFAULT 1,           -- replace-by-snapshot不可な受信先は0
    hmac_enabled     BIT          NOT NULL DEFAULT 0,
    hmac_secret_ref  NVARCHAR(200) NULL,                        -- Key Vaultのシークレット名（値は持たない）
    rate_limit_rps   INT          NULL,                         -- テナント別スロットリング(req/sec)
    is_active        BIT          NOT NULL DEFAULT 1,
    created_at       DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
);
```

### 4.2 destination_header（任意の認証/カスタムヘッダー）
```sql
CREATE TABLE dbo.destination_header (
    header_id      INT IDENTITY PRIMARY KEY,
    destination_id INT          NOT NULL REFERENCES dbo.destination(destination_id),
    header_name    NVARCHAR(128) NOT NULL,        -- 例: Authorization, X-API-KEY
    value_ref      NVARCHAR(200) NOT NULL,        -- Key Vaultシークレット名（機密値はDBに置かない）
    CONSTRAINT UQ_header UNIQUE (destination_id, header_name)
);
```
- **機密値はすべて Key Vault**。DBにはシークレット名（参照）のみ。Functions側でTTLキャッシュ（基本設計5.1）。

---

## 5. セッション系（一時状態）

### 5.1 session
```sql
CREATE TABLE dbo.session (
    session_id     BIGINT IDENTITY PRIMARY KEY,
    public_id      UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() UNIQUE, -- 端末/API公開用
    tenant_id      INT          NOT NULL REFERENCES dbo.tenant(tenant_id),
    type           VARCHAR(16)  NOT NULL,         -- 'shipment'(伝票) / 'inventory'(棚卸)
    business_key   NVARCHAR(128) NULL,            -- 伝票番号 / 棚卸キャンペーンID
    status         VARCHAR(16)  NOT NULL DEFAULT 'open', -- open→finalized→forwarded→archived→purged
    expected_count INT          NULL,             -- 伝票: 端末申告のユニーク件数(total_count)
    created_at     DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    last_event_at  DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), -- タイムアウト判定用
    finalized_at   DATETIME2(3) NULL,
    forwarded_at   DATETIME2(3) NULL
);
CREATE INDEX IX_session_tenant_status ON dbo.session(tenant_id, status) INCLUDE(type, last_event_at);
CREATE INDEX IX_session_business ON dbo.session(tenant_id, type, business_key);
```
- **伝票の再読取＝再転送**: 同一 `business_key` で複数 `session` を許容（再オープンは新セッション）。最新の `open`/直近セッションを「現在」とする。受信側冪等性は `snapshot.idempotency_key` で担保。
- **status遷移**: `open`（棚卸は仮確定転送を繰り返してよい）→ `finalized`（確定）→ `forwarded`（最終転送成功）→ `archived` → `purged`。
- **未確定タイムアウト**: `last_event_at` から一定時間無活動の `open` セッションは、運用ポリシーで自動確定 or 警告（基本設計6）。

### 5.2 reading（ホットテーブル・後勝ち）
```sql
CREATE TABLE dbo.reading (
    reading_id  BIGINT IDENTITY PRIMARY KEY,       -- クラスタ化（単調増加で挿入高速）
    session_id  BIGINT       NOT NULL REFERENCES dbo.session(session_id),
    tenant_id   INT          NOT NULL,
    epc         VARBINARY(32) NOT NULL,            -- 生EPC（シリアル保持＝個体識別・将来の明細転送用）
    search_key  VARBINARY(32) NULL,                -- epc & mask（SKU特定用）。rawモード/マスク未設定時はNULL
    location    NVARCHAR(64) NULL,                 -- 棚卸: 棚番/エリアID
    device_id   NVARCHAR(128) NULL,
    read_at     DATETIME2(3) NOT NULL,
    excluded    BIT          NOT NULL DEFAULT 0,    -- 論理削除（過剰読取の除外, 将来UI）
    updated_at  DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_reading UNIQUE (session_id, epc)  -- 後勝ちUPSERTのキー
);
-- 集約（SKU別数量）
CREATE INDEX IX_reading_agg ON dbo.reading(session_id, search_key) INCLUDE(excluded);
-- 棚卸: ロケ別明細
CREATE INDEX IX_reading_loc ON dbo.reading(session_id, location, search_key) INCLUDE(excluded);
```
- **後勝ち（last-write-wins）**: `(session_id, epc)` で MERGE/UPSERT。既存行があれば `location, search_key, device_id, read_at, updated_at` を上書き（tag1をAで読んだ後Bで読めばBに収束）。
- **集約時は `excluded = 0` のみ**を対象。
- クラスタ化キーは `reading_id`(単調増加)で挿入性能を確保。重複判定・後勝ちは `UQ_reading(session_id, epc)` を使用。

#### MERGE（後勝ちUPSERT）の例
```sql
MERGE dbo.reading WITH (HOLDLOCK) AS t
USING (SELECT @session_id AS session_id, @epc AS epc) AS s
   ON (t.session_id = s.session_id AND t.epc = s.epc)
WHEN MATCHED THEN UPDATE SET
   t.search_key = @search_key, t.location = @location,
   t.device_id = @device_id, t.read_at = @read_at, t.updated_at = SYSUTCDATETIME(),
   t.excluded = 0
WHEN NOT MATCHED THEN INSERT
   (session_id, tenant_id, epc, search_key, location, device_id, read_at)
   VALUES (@session_id, @tenant_id, @epc, @search_key, @location, @device_id, @read_at);
```
- マイクロバッチ（1秒/100件）では **テーブル値パラメータ (TVP) によるバルクMERGE** を推奨（往復削減）。

### 5.3 snapshot（転送の記録・再送・監査）
```sql
CREATE TABLE dbo.snapshot (
    snapshot_id     BIGINT IDENTITY PRIMARY KEY,
    session_id      BIGINT       NOT NULL REFERENCES dbo.session(session_id),
    destination_id  INT          NOT NULL REFERENCES dbo.destination(destination_id),
    version         INT          NOT NULL,         -- セッション内で単調増加（仮確定の世代）
    is_final        BIT          NOT NULL,
    idempotency_key UNIQUEIDENTIFIER NOT NULL,      -- 受信側の重複排除用（ペイロードにも載せる）
    item_count      INT          NOT NULL,          -- 集約後のSKU件数
    status          VARCHAR(16)  NOT NULL DEFAULT 'pending', -- pending/sent/failed/dead
    attempt_count   INT          NOT NULL DEFAULT 0,
    created_at      DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    sent_at         DATETIME2(3) NULL,
    CONSTRAINT UQ_snapshot_ver UNIQUE (session_id, destination_id, version)
);
CREATE INDEX IX_snapshot_status ON dbo.snapshot(tenant_id, status) ; -- 失敗ログUI/再送
```
- `version` 単調増加＋`is_final` で、受信側が「最新版で置換」「最終版判定」できる（基本設計5.2.1）。
- `status='dead'` が基本設計5.4のポイズン相当。管理画面の「再送」は新 `snapshot` を起票して再送する。
- 注: 上の `IX_snapshot_status` で `tenant_id` を使うため、列を追加するか `session` 結合に変更（実装時に確定）。

### 5.4 delivery_attempt（送信試行ログ）
```sql
CREATE TABLE dbo.delivery_attempt (
    attempt_id    BIGINT IDENTITY PRIMARY KEY,
    snapshot_id   BIGINT       NOT NULL REFERENCES dbo.snapshot(snapshot_id),
    attempted_at  DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    http_status   INT          NULL,
    error         NVARCHAR(1000) NULL,
    duration_ms   INT          NULL
);
```

---

## 6. 集約クエリ（転送ペイロード生成）

### 6.1 SKU別数量（aggregateモード）
```sql
SELECT p.sku, COUNT(*) AS quantity
FROM dbo.reading r
JOIN dbo.product p ON p.tenant_id = r.tenant_id AND p.search_key = r.search_key
WHERE r.session_id = @session_id AND r.excluded = 0
GROUP BY p.sku;
```

### 6.2 ロケ別SKU明細（棚卸の端末フィードバック）
```sql
SELECT r.location, p.sku, COUNT(*) AS quantity
FROM dbo.reading r
JOIN dbo.product p ON p.tenant_id = r.tenant_id AND p.search_key = r.search_key
WHERE r.session_id = @session_id AND r.excluded = 0
GROUP BY r.location, p.sku;
```
- 数十万タグでも SKU集約後は数千件規模に収束し、ペイロードは軽量（基本設計5.2.1）。

### 6.3 raw モード（マスタ無し・EPCそのまま連携）
```sql
SELECT r.location, r.epc, r.read_at
FROM dbo.reading r
WHERE r.session_id = @session_id AND r.excluded = 0;
```
- `product` 結合なし。SKU解決をせず、セッション（必要ならロケ）単位で生EPC（シリアル）をそのまま連携。
- 「何らかの単位で読んでシリアルだけ連携したい」要件に対応する最も薄いモード。

### 6.4 未知タグレーン（aggregate/detailモードで商品マスタに該当なし）
```sql
SELECT r.epc, r.search_key, r.read_at
FROM dbo.reading r
LEFT JOIN dbo.product p ON p.tenant_id = r.tenant_id AND p.search_key = r.search_key
WHERE r.session_id = @session_id AND r.excluded = 0 AND p.sku IS NULL;
```
- 該当SKUが無いEPCは **「未知タグ」レーン**として本体ペイロードと分離し、件数とともに端末/管理画面へ**通知**する（マスタ未登録の検知）。本体の集約結果には混ぜない。

---

## 7. マルチテナント・セキュリティ

- すべてのクエリで `tenant_id` を必須フィルタ。アプリ層でテナント解決（IoT Hub device twin 由来、基本設計4.1）。
- 多層防御として **Row-Level Security**（`SESSION_CONTEXT` にtenant_idを設定し、セキュリティポリシーで強制）を検討。
- 機密（接続文字列/トークン/HMAC鍵）は **Key Vault**。DBは参照名のみ保持。

---

## 8. 保持・ライフサイクル（基本設計6）

- 起点は `session.forwarded_at`（最終転送成功）。テナント別TTL経過後、タイマーFunctionsで:
  1. `reading` を Blob にアーカイブ（任意）→ 削除、`session.status='archived'`。
  2. さらに猶予後 `purged`。
- `open`（仮確定中）はパージしない。
- PoCはパージ無効（全保持）。

---

## 9. 決定事項・未決事項

### 決定済み
1. **EPC/検索キーの型**: `VARBINARY(32)`（SGTIN-96/198を内包）。
2. **伝票の再読取**: 同一 `business_key` で**新セッション**を払い出す（履歴保持・冪等性キー単位が明確）。
3. **未知タグ**（商品マスタ未登録EPC）: 本体と分離した**「未知タグ」レーンで通知**（§6.4）。
4. **商品マスタ任意（rawモード）**: マスク/マスタ無しで生EPCをそのまま連携するモードを許容（§4.1, §6.3）。

### 未決（実装時に確定）
1. `snapshot` の `tenant_id` 列追加 or `session` 結合（インデックス設計）。
2. `reading` 高ボリューム時のパーティショニング/インデックス再構成方針（棚卸ピーク）。
3. SQL アクセスは Dapper（ホットパス）＋ EF Core（設定/マスタ）の併用か単一か。
4. rawモードでの重複排除キー: `epc` のみで後勝ち（マスク非依存）。マスク有無を `destination`/`session` のどちらで決定するか（テナント既定＋宛先上書き想定）。
