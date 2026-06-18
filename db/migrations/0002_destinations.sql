-- 0002_destinations.sql  正本: docs/design/data-model.md §3.2/§4
-- NOTE(poc): FK constraints (destination_header→destination, *→tenant) omitted for PoC; add before production.
IF OBJECT_ID('dbo.destination') IS NULL
CREATE TABLE dbo.destination (
    destination_id    INT IDENTITY PRIMARY KEY,
    tenant_id         INT          NOT NULL,
    name              NVARCHAR(200) NOT NULL,
    url               NVARCHAR(2048) NOT NULL,
    http_method       VARCHAR(8)   NOT NULL CONSTRAINT DF_dest_method DEFAULT 'POST',
    payload_mode      VARCHAR(16)  NOT NULL CONSTRAINT DF_dest_mode DEFAULT 'aggregate',
    schema_version    NVARCHAR(16) NOT NULL CONSTRAINT DF_dest_schema DEFAULT '1',
    allow_provisional BIT          NOT NULL CONSTRAINT DF_dest_prov DEFAULT 1,
    hmac_enabled      BIT          NOT NULL CONSTRAINT DF_dest_hmac DEFAULT 0,
    hmac_secret_ref   NVARCHAR(200) NULL,
    rate_limit_rps    INT          NULL,
    is_active         BIT          NOT NULL CONSTRAINT DF_dest_active DEFAULT 1,
    created_at        DATETIMEOFFSET(3) NOT NULL CONSTRAINT DF_dest_created DEFAULT SYSDATETIMEOFFSET()
);

IF OBJECT_ID('dbo.destination_header') IS NULL
CREATE TABLE dbo.destination_header (
    header_id      INT IDENTITY PRIMARY KEY,
    destination_id INT          NOT NULL,
    header_name    NVARCHAR(128) NOT NULL,
    value_ref      NVARCHAR(200) NOT NULL,  -- Key Vault シークレット名(機密値そのものは持たない)
    CONSTRAINT UQ_dest_header UNIQUE (destination_id, header_name)
);

-- v1パイプラインは固定SGTIN-96マスクを使用。本テーブルはマルチマスク(将来)用にスキーマだけ用意。
IF OBJECT_ID('dbo.mask') IS NULL
CREATE TABLE dbo.mask (
    mask_id      INT IDENTITY PRIMARY KEY,
    tenant_id    INT           NOT NULL,
    scheme       VARCHAR(32)   NOT NULL,
    mask_value   VARBINARY(32) NOT NULL,
    header_match VARBINARY(2)  NULL,
    is_active    BIT           NOT NULL CONSTRAINT DF_mask_active DEFAULT 1,
    created_at   DATETIMEOFFSET(3) NOT NULL CONSTRAINT DF_mask_created DEFAULT SYSDATETIMEOFFSET()
);
