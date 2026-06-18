-- 0001_initial.sql  正本: docs/design/data-model.md（本PoCはGUID参照・最小テーブルに簡略化）
IF OBJECT_ID('dbo.tenant') IS NULL
CREATE TABLE dbo.tenant (
    tenant_id   INT IDENTITY PRIMARY KEY,
    code        NVARCHAR(64)  NOT NULL UNIQUE,
    name        NVARCHAR(200) NOT NULL,
    status      VARCHAR(16)   NOT NULL CONSTRAINT DF_tenant_status DEFAULT 'active',
    created_at  DATETIMEOFFSET(3)  NOT NULL CONSTRAINT DF_tenant_created DEFAULT SYSDATETIMEOFFSET()
);

IF OBJECT_ID('dbo.product') IS NULL
CREATE TABLE dbo.product (
    tenant_id    INT           NOT NULL,
    search_key   VARBINARY(32) NOT NULL,
    sku          NVARCHAR(64)  NOT NULL,
    item_code    NVARCHAR(64)  NULL,
    color        NVARCHAR(32)  NULL,
    size         NVARCHAR(32)  NULL,
    description  NVARCHAR(200) NULL,
    updated_at   DATETIMEOFFSET(3)  NOT NULL CONSTRAINT DF_product_updated DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT PK_product PRIMARY KEY (tenant_id, search_key)
);

IF OBJECT_ID('dbo.session') IS NULL
CREATE TABLE dbo.session (
    public_id      UNIQUEIDENTIFIER PRIMARY KEY,
    tenant_id      INT          NOT NULL,
    type           VARCHAR(16)  NOT NULL,
    business_key   NVARCHAR(128) NULL,
    status         VARCHAR(16)  NOT NULL,
    resolve_sku    BIT          NOT NULL CONSTRAINT DF_session_resolve DEFAULT 1,
    expected_count INT          NULL,
    created_at     DATETIMEOFFSET(3) NOT NULL,
    last_event_at  DATETIMEOFFSET(3) NOT NULL,
    finalized_at   DATETIMEOFFSET(3) NULL,
    forwarded_at   DATETIMEOFFSET(3) NULL
);

IF OBJECT_ID('dbo.reading') IS NULL
CREATE TABLE dbo.reading (
    reading_id  BIGINT IDENTITY PRIMARY KEY,
    session_id  UNIQUEIDENTIFIER NOT NULL,
    tenant_id   INT           NOT NULL,
    epc         VARBINARY(32) NOT NULL,
    search_key  VARBINARY(32) NULL,
    location_l1 NVARCHAR(64)  NULL,
    location_l2 NVARCHAR(64)  NULL,
    location_l3 NVARCHAR(64)  NULL,
    device_id   NVARCHAR(128) NULL,
    read_at     DATETIMEOFFSET(3)  NOT NULL,
    excluded    BIT           NOT NULL CONSTRAINT DF_reading_excluded DEFAULT 0,
    updated_at  DATETIMEOFFSET(3)  NOT NULL CONSTRAINT DF_reading_updated DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT UQ_reading UNIQUE (session_id, epc)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_reading_agg')
CREATE INDEX IX_reading_agg ON dbo.reading(session_id, search_key) INCLUDE(excluded);

IF OBJECT_ID('dbo.snapshot') IS NULL
CREATE TABLE dbo.snapshot (
    snapshot_id     BIGINT IDENTITY PRIMARY KEY,
    tenant_id       INT          NOT NULL,
    session_id      UNIQUEIDENTIFIER NOT NULL,
    version         INT          NOT NULL,
    is_final        BIT          NOT NULL,
    idempotency_key UNIQUEIDENTIFIER NOT NULL,
    item_count      INT          NOT NULL,
    success         BIT          NOT NULL,
    created_at      DATETIMEOFFSET(3) NOT NULL CONSTRAINT DF_snapshot_created DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT UQ_snapshot_ver UNIQUE (session_id, version)
);
