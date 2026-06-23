-- 0003: reading の一括 UPSERT 用テーブル値パラメータ型(TVP)。
-- reads バッチ取込で SqlReadingStore.UpsertBatch が MERGE のソースに使う。
-- dbo.reading の対応列と型を一致させる(epc/search_key=VARBINARY(32), location=NVARCHAR(64), device_id=NVARCHAR(128))。
IF TYPE_ID('dbo.ReadingTvp') IS NULL
CREATE TYPE dbo.ReadingTvp AS TABLE (
    epc         VARBINARY(32)     NOT NULL,
    search_key  VARBINARY(32)     NULL,
    device_id   NVARCHAR(128)     NULL,
    read_at     DATETIMEOFFSET(3) NOT NULL,
    location_l1 NVARCHAR(64)      NULL,
    location_l2 NVARCHAR(64)      NULL,
    location_l3 NVARCHAR(64)      NULL
);
