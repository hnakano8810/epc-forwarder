# 詳細設計：reads バッチ取込

最終更新 2026-06-23。対象: 取込経路を「EPC 1個 = 1 D2C メッセージ」から「EPC 配列 = 1 メッセージ」へ移行する。

## 背景・課題

現行契約は `kind:"read"`（EPC 1個・ストリーミング）。RFID 規模（伝票あたり数百枚、棚卸で数万〜数十万件）では、IoT Hub のメッセージ数課金（4KB 単位）とスロットル、および 1 読取ごとの関数起動＋単行 SQL UPSERT が費用・性能の両面で破綻する。下流（EventHub トリガーの `IsBatched`、セッション単位集約）はもともと配列前提で設計されているため、**エッジ→IoT Hub のメッセージ粒度だけが未配列**。これを `reads`（配列）へ統一する。

入出荷・棚卸の両業務とも配列化する（伝票あたり数百枚はざらにあるため、入出荷も少量前提が崩れる）。

## スコープ

**やる:**
- `reads`（配列）wire 契約＋パーサ（単数 `read` は廃止＝置換）
- Core を配列コマンドへ（`ReadBatchCommand`）。後勝ち dedup を Core に置く
- `IReadingStore` を一括 UPSERT（`UpsertBatch`）へ。TVP + MERGE 実装
- migration `0003` で UDT `dbo.ReadingTvp` 追加
- 取込テスト（Core / Functions / Infrastructure）
- `ingestion-contract.md` 更新（非規範のエッジガイダンス追記）

**やらない（対象外）:**
- settle 窓 / 再突合（complete が read を追い越す既知ギャップ）はそのまま。サーバはフラッシュ順序に依存せず正しく動く
- 複数宛先ファンアウト（先頭のみ）
- 認証・クエリ API・棚卸 HTTP 経路（取込経路に局所化）

## 契約（wire）

`kind:"reads"` に一本化（`kind:"read"` は削除）。`kind:"complete"` は不変。

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

- **shared（メッセージ共通）**: `tenant` / `session_id` / `business_key` / `session_type` / `resolve_sku` / `device_id`
- **per-read（`epcs[]` 各要素）**: `epc` / `read_at`（後勝ち判定に必須） / `location`（任意。棚卸の別ロケ再読で後勝ち収束に使用）
- 遅延セッション生成: 未知 `session_id` を持つ最初のメッセージで session 生成（`tenant`/`session_type`/`business_key` を使用）。以後はメタデータを上書きしない
- `session_type` は `shipment` / `inventory`（大文字小文字非依存）。不正値はメッセージ skip

## ドメイン表現（Core）

```csharp
// EpcForwarder.Core.Ingestion
public sealed record ReadEntry(string Epc, DateTimeOffset ReadAt, ReadLocation? Location);

public sealed record ReadBatchCommand(
    int Tenant,
    Guid SessionId,
    string? BusinessKey,
    SessionType SessionType,
    bool ResolveSku,
    string? DeviceId,
    IReadOnlyList<ReadEntry> Reads) : IIngestionCommand;
```
`ReadCommand`（単数）は削除。`CompleteCommand` / `CompletionOutcome` は不変。

## データフロー（1 メッセージ）

1. `IngestionFunction` が `string[] messages` を受信（トリガー側バッチは現状維持）→ 各要素をパース
2. `kind:"reads"` → `ReadBatchCommand`（N 件）。`session_type` 不正は `FormatException` で skip（不変）
3. `IngestionDispatcher.IngestReads`: 未知セッションを **1 回だけ** 遅延生成
4. `ReadingIngestor.IngestBatch`:
   - **後勝ち dedup**: `Reads` を `Epc` でグルーピングし最大 `ReadAt` の要素を採用（MERGE のソース重複エラー回避。README の後勝ち UPSERT と意味論一致）
   - `ResolveSku` 時、各 EPC に `Sgtin96.DeriveSearchKey(epcHex)` で検索キー付与（`false` なら null）
   - `readings.UpsertBatch(sessionId, entries)` を **1 回** 呼ぶ
   - `session.Touch(clock.UtcNow)` → 保存を **1 回**
5. `SqlReadingStore.UpsertBatch`: `DataTable` を TVP として渡し、`MERGE dbo.reading WITH (HOLDLOCK)` を 1 回。既存の後勝ち列（search_key/device_id/read_at/location/updated_at/excluded=0）をそのまま。`tenant_id` は `(SELECT tenant_id FROM dbo.session WHERE public_id=@SessionId)`、`session_id` はスカラー、device_id を含む各行は TVP（バッチ共通の device_id を ingestor が全行に刻む）

## SQL（TVP + MERGE）

migration `0003_reading_tvp.sql`（冪等）:

```sql
IF TYPE_ID('dbo.ReadingTvp') IS NULL
CREATE TYPE dbo.ReadingTvp AS TABLE (
    epc         VARBINARY(32) NOT NULL,
    search_key  VARBINARY(32) NULL,
    device_id   NVARCHAR(128) NULL,
    read_at     DATETIMEOFFSET(3) NOT NULL,
    location_l1 NVARCHAR(64) NULL,
    location_l2 NVARCHAR(64) NULL,
    location_l3 NVARCHAR(64) NULL
);
```

MERGE（概形）:
```sql
MERGE dbo.reading WITH (HOLDLOCK) AS t
USING @rows AS s
   ON t.session_id = @SessionId AND t.epc = s.epc
WHEN MATCHED THEN UPDATE SET
   search_key = s.search_key, device_id = s.device_id, read_at = s.read_at,
   location_l1 = s.location_l1, location_l2 = s.location_l2, location_l3 = s.location_l3,
   updated_at = SYSDATETIMEOFFSET(), excluded = 0
WHEN NOT MATCHED THEN INSERT
   (session_id, tenant_id, epc, search_key, device_id, read_at, location_l1, location_l2, location_l3)
   VALUES (@SessionId,
           (SELECT tenant_id FROM dbo.session WHERE public_id = @SessionId),
           s.epc, s.search_key, s.device_id, s.read_at, s.location_l1, s.location_l2, s.location_l3);
```
- `device_id` は TVP の列（バッチ共通値を ingestor が全行に刻む）。`@rows` は `dbo.ReadingTvp` 型の TVP（Dapper `DataTable.AsTableValuedParameter`）
- C# で dedup 済みのため MERGE ソースに `epc` 重複は無い（"same row more than once" を構造的に回避）

## port 変更

```csharp
public interface IReadingStore
{
    void UpsertBatch(Guid sessionId, IReadOnlyList<ReadingEntry> entries); // (session,epc) 後勝ち一括
    void Upsert(Guid sessionId, ReadingEntry entry);                       // 便宜: UpsertBatch([entry]) へ委譲
    IReadOnlyList<ReadingEntry> List(Guid sessionId);
    int CountUnique(Guid sessionId);
}
```

実装判断（churn 最小化・intent 維持）:
- `ReadingEntry` は現状維持 `(Epc, SearchKey, DeviceId, ReadAt, Location)`。grep で `ReadingEntry.DeviceId` は `List` 消費側（SnapshotPublisher / SessionQueryService）で未使用＝書き込み専用と確認。device_id は **TVP の列**として各行に持たせる（バッチ共通値を ingestor が全行に刻む）。MERGE は `s.device_id` を使用
- 単行 `Upsert` は **便宜メソッドとして残す**が、実装は `UpsertBatch([entry])` へ委譲＝**SQL の単行専用経路は消える**（spec の intent「単行 SQL 経路を無くす」は満たす）。テスト約20か所が状態セットアップで `Upsert` を使うため、これにより無改変で済む

## エラー処理・冪等性（原則は不変）

- 不正メッセージ（JSON 不正・未知 kind・不正 session_type）→ ログのみ skip（毒メッセージ停滞防止）
- DB / 配信例外 → 伝播し EventHub リトライ（at-least-once）
- **冪等**: 再送バッチは同じ `(session, epc)` を後勝ちで再 UPSERT＝無害。空 `epcs` の reads は no-op（セッション遅延生成のみ発生しうる）
- `complete` の forwarded 冪等は不変

## 契約ドキュメント追記（非規範ガイダンス）

`docs/design/ingestion-contract.md` に:
- `complete` 送信前に未送信 read を **flush 推奨**（settle 窓ギャップの緩和。ただしサーバは順序非依存で正しく動作する）
- エッジは **1 メッセージ ≤ 約 2000 件 かつ ≤ 256KB** でチャンク分割（棚卸の数十万件は複数メッセージ前提）
- settle 窓 / 再突合は対象外（既知ギャップのまま明記）

## テスト

- **Core (`ReadingIngestor` / `IngestionDispatcher`)**: 後勝ち dedup（同 EPC×2 で最新 `read_at` 採用）、per-EPC 鍵導出、`resolve_sku=false` で SearchKey=null、`UpsertBatch` 1 回・session `Touch`/`Save` 1 回（fake store で検証）、空 `epcs` の扱い
- **Functions (`IngestionMessageParser`)**: `reads` 配列 → `ReadBatchCommand`（N>1 と N=1）、未知 `session_type` で `FormatException`、JSON 不正 skip、`kind:"read"` が未知 kind として skip されること（廃止の確認）
- **Infrastructure (`SqlReadingStore`, Testcontainers)**: migration `0003` 適用後、`UpsertBatch` の一括投入、別ロケ再読の後勝ち（location 上書き）、再送（同バッチ再適用）で件数不変、`CountUnique` 整合

## 影響ファイル

- `src/EpcForwarder.Functions/Ingestion/IngestionMessages.cs`（`ReadsMessage` + `ReadEntryDto`、`ReadMessage` 削除）
- `src/EpcForwarder.Functions/Ingestion/IngestionMessageParser.cs`（`reads` → `ReadBatchCommand`、`read` 削除）
- `src/EpcForwarder.Functions/Ingestion/IngestionFunction.cs`（switch を `ReadBatchCommand` へ）
- `src/EpcForwarder.Core/Ingestion/IngestionCommands.cs`（`ReadEntry` + `ReadBatchCommand`、`ReadCommand` 削除）
- `src/EpcForwarder.Core/Sessions/ReadingIngestor.cs`（`IngestBatch`）
- `src/EpcForwarder.Core/Ingestion/IngestionDispatcher.cs`（`IngestReads`）
- `src/EpcForwarder.Core/Abstractions/Ports.cs`（`IReadingStore.UpsertBatch`、`ReadingEntry` 調整）
- `src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs`（TVP + MERGE）
- `db/migrations/0003_reading_tvp.sql`（新規）
- `docs/design/ingestion-contract.md`（更新）
- 各テストプロジェクト
