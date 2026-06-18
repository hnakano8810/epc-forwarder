# Azureアダプタ① Azure SQL 永続化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** インプロセスで検証済みのドメイン（伝票/棚卸/GTIN投入）が依存するポートのうち、永続化系（`ISessionStore` / `IReadingStore` / `IProductCatalog`＋`IProductWriteStore` / `ISnapshotStore`）を **Azure SQL Database** 実装で提供する。Docker 上の SQL Server コンテナに対する統合テストで、in-memory フェイクと同じ契約（後勝ち・version単調増加・SKU解決・セッション状態の往復）を満たすことを検証する。

**Architecture:** `EpcForwarder.Infrastructure/Persistence` に Dapper + Microsoft.Data.SqlClient（同期）で各リポジトリを実装する。スキーマは `db/migrations/*.sql`（data-model.md 準拠）を正本とし、Infrastructure に埋め込みリソースとして取り込んで `MigrationRunner` で適用する。ドメイン `Session` 集約は状態遷移を強制するため、DBからの再構築用に `Session.Rehydrate(...)` ファクトリを追加する。統合テストは Testcontainers.MsSql で SQL Server を起動して実行する。

**Tech Stack:** C# / .NET 8、Dapper、Microsoft.Data.SqlClient、xUnit、Testcontainers.MsSql、Docker（ローカルにあり）。

**Scope boundary:**
- 含む: SQLスキーマ（tenant / product / session / reading / snapshot）、`MigrationRunner`、上記4ポートのSQL実装、`Session.Rehydrate`、Testcontainers 統合テスト、DI登録拡張。
- 含まない（後続サブ計画）: Key Vault `ISecretStore`、`IDeviceFeedback`(C2D)、IoT Hubトリガー/HTTP API（Functions host）、Bicep、宛先設定(`destination`/`mask`)のDB読み込み、`delivery_attempt`、ロケ別取込。
- **検証範囲（合意済み）**: ローカル（Dockerコンテナ）統合テストまで。実機Azureデプロイは後続のBicep/手順書で。

**Prerequisites:** `dotnet` は ~/.dotnet。各コマンド前に `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH`。Docker 稼働必須（統合テストが SQL Server コンテナを起動。初回はイメージ取得で数分）。全テスト: `dotnet test EpcForwarder.sln --nologo`。`Directory.Build.props` は `TreatWarningsAsErrors=true`。

**設計上の決定（data-model.md からのPoC簡略化、明記して逸脱）:**
1. SQLアクセスは **Dapper + Microsoft.Data.SqlClient（同期）**。ポートが同期のため。EF採用案は撤回（ユーザーが「管理が楽な方」と委任済み；SQL先行のスキーマと同期ポートにはDapper+手書きSQLが素直）。async化は将来。
2. `reading`/`snapshot` はセッションを **`public_id`(GUID) で参照**（ポートがGUIDのみ流通させるため）。data-model の bigint-identity 内部キー最適化は将来。
3. `snapshot` に `destination_id` は持たない（`DeliveryTarget` は当面メモリ渡し。`SnapshotRecord` ドメインにも無い）。
4. `reading` の `location_l1/l2/l3` 列は作るが当面未投入（ロケ付き取込は別スライス）。
5. `NextVersion` は `MAX(version)+1` のベストエフォート（セッション単位=単一ライタ想定。`UQ_snapshot_ver` が競合を検出）。
6. 当面使う5テーブルのみ作成。`mask`/`destination`/`destination_header`/`delivery_attempt` は設定読み込みサブ計画で追加。

---

## File Structure

| ファイル | 責務 |
|---|---|
| `db/migrations/0001_initial.sql` | スキーマ（tenant/product/session/reading/snapshot）。既存プレースホルダを置換 |
| `src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj`（変更） | Dapper / Microsoft.Data.SqlClient 追加、`db/migrations/*.sql` を埋め込み |
| `src/EpcForwarder.Infrastructure/Persistence/MigrationRunner.cs` | 埋め込みSQLを順に適用 |
| `src/EpcForwarder.Infrastructure/Persistence/SqlConnectionFactory.cs` | 接続生成 |
| `src/EpcForwarder.Infrastructure/Persistence/SqlSessionStore.cs` | `ISessionStore` 実装 |
| `src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs` | `IReadingStore` 実装（MERGE後勝ち） |
| `src/EpcForwarder.Infrastructure/Persistence/SqlSnapshotStore.cs` | `ISnapshotStore` 実装 |
| `src/EpcForwarder.Infrastructure/Persistence/SqlProductStore.cs` | `IProductCatalog`+`IProductWriteStore` 実装 |
| `src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs` | `AddSqlPersistence(connectionString)` |
| `src/EpcForwarder.Core/Sessions/Session.cs`（変更） | `Rehydrate(...)` ファクトリ追加 |
| `tests/EpcForwarder.Infrastructure.Tests/`（新規プロジェクト） | Testcontainers 統合テスト一式 |

統合テストは Core.Tests とは別プロジェクト（`EpcForwarder.Infrastructure.Tests`）に置く（Testcontainers/Docker依存をユニットテストから隔離）。

---

## Task 1: `Session.Rehydrate` ファクトリ（DB再構築用）

`Session` は `Open` でしか生成できず遷移で状態が変わる。DBから任意状態で復元するためのファクトリを追加する。

**Files:**
- Modify: `src/EpcForwarder.Core/Sessions/Session.cs`
- Test: `tests/EpcForwarder.Core.Tests/Sessions/SessionRehydrateTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Sessions/SessionRehydrateTests.cs
using EpcForwarder.Core.Sessions;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class SessionRehydrateTests
{
    [Fact]
    public void Rehydrate_RestoresAllFields_WithoutTransitionChecks()
    {
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UnixEpoch;
        var finalized = created.AddMinutes(1);

        var s = Session.Rehydrate(
            publicId: id, tenantId: 7, type: SessionType.Inventory, businessKey: "CAMP-9",
            status: SessionStatus.Forwarded, expectedCount: 42,
            createdAt: created, lastEventAt: finalized, finalizedAt: finalized, forwardedAt: finalized);

        Assert.Equal(id, s.PublicId);
        Assert.Equal(7, s.TenantId);
        Assert.Equal(SessionType.Inventory, s.Type);
        Assert.Equal("CAMP-9", s.BusinessKey);
        Assert.Equal(SessionStatus.Forwarded, s.Status); // 遷移を経ずに直接 Forwarded
        Assert.Equal(42, s.ExpectedCount);
        Assert.Equal(finalized, s.FinalizedAt);
        Assert.Equal(finalized, s.ForwardedAt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~SessionRehydrateTests`
Expected: コンパイル失敗（`Rehydrate` 未定義）。

- [ ] **Step 3: Add the factory**

`Session.cs` に、既存メンバを変更せず以下を追加（クラス内）。`ExpectedCount` 等は既存の private setter を使うため、フィールドを直接設定する private ctor を足す:

```csharp
    // DB等からの再構築専用。状態遷移チェックを経ずに全フィールドを復元する。
    public static Session Rehydrate(
        Guid publicId,
        int tenantId,
        SessionType type,
        string? businessKey,
        SessionStatus status,
        int? expectedCount,
        DateTimeOffset createdAt,
        DateTimeOffset lastEventAt,
        DateTimeOffset? finalizedAt,
        DateTimeOffset? forwardedAt)
    {
        var s = new Session(publicId, tenantId, type, businessKey, createdAt);
        s.Status = status;
        s.ExpectedCount = expectedCount;
        s.LastEventAt = lastEventAt;
        s.FinalizedAt = finalizedAt;
        s.ForwardedAt = forwardedAt;
        return s;
    }
```

（`Status`/`ExpectedCount`/`LastEventAt`/`FinalizedAt`/`ForwardedAt` の setter は既に `private set`。同一クラス内なので代入可能。`CreatedAt` は ctor で設定済み。）

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~SessionRehydrateTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Sessions/Session.cs tests/EpcForwarder.Core.Tests/Sessions/SessionRehydrateTests.cs
git commit -m "feat(core): Session.Rehydrate(DB再構築用ファクトリ)"
```

---

## Task 2: スキーマ ＋ パッケージ ＋ MigrationRunner ＋ 接続ファクトリ

**Files:**
- Modify: `db/migrations/0001_initial.sql`（プレースホルダを置換）
- Modify: `src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj`
- Create: `src/EpcForwarder.Infrastructure/Persistence/MigrationRunner.cs`, `src/EpcForwarder.Infrastructure/Persistence/SqlConnectionFactory.cs`

- [ ] **Step 1: Write the schema**

`db/migrations/0001_initial.sql` の内容を全置換:

```sql
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
```

- [ ] **Step 2: Add packages and embed the migrations**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add src/EpcForwarder.Infrastructure package Dapper
dotnet add src/EpcForwarder.Infrastructure package Microsoft.Data.SqlClient
```
Then edit `src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj`: add an ItemGroup to embed the migration scripts (path is repo-root relative to the project):

```xml
  <ItemGroup>
    <EmbeddedResource Include="..\..\db\migrations\*.sql">
      <Link>Migrations\%(Filename)%(Extension)</Link>
      <LogicalName>EpcForwarder.Migrations.%(Filename)%(Extension)</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
```

- [ ] **Step 3: Write the connection factory and migration runner**

```csharp
// src/EpcForwarder.Infrastructure/Persistence/SqlConnectionFactory.cs
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlConnectionFactory(string connectionString)
{
    public SqlConnection Create() => new(connectionString);
}
```

```csharp
// src/EpcForwarder.Infrastructure/Persistence/MigrationRunner.cs
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

/// <summary>埋め込まれた db/migrations/*.sql を名前順に適用する(冪等: スクリプトは IF NOT EXISTS で書く)。</summary>
public static class MigrationRunner
{
    public static void Apply(string connectionString)
    {
        var asm = typeof(MigrationRunner).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("EpcForwarder.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        foreach (var name in names)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build EpcForwarder.sln --nologo -v q`
Expected: 0 警告 / 0 エラー。

- [ ] **Step 5: Commit**

```bash
git add db/migrations/0001_initial.sql src/EpcForwarder.Infrastructure/
git commit -m "feat(infra): SQLスキーマ・パッケージ・MigrationRunner・接続ファクトリ"
```

---

## Task 3: 統合テストプロジェクト ＋ Testcontainers フィクスチャ

**Files:**
- Create: `tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj`
- Create: `tests/EpcForwarder.Infrastructure.Tests/SqlServerFixture.cs`
- Modify: `EpcForwarder.sln`（プロジェクト追加）

- [ ] **Step 1: Create the test project and add references/packages**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet new xunit -n EpcForwarder.Infrastructure.Tests -o tests/EpcForwarder.Infrastructure.Tests -f net8.0
dotnet sln add tests/EpcForwarder.Infrastructure.Tests
dotnet add tests/EpcForwarder.Infrastructure.Tests reference src/EpcForwarder.Infrastructure src/EpcForwarder.Core
dotnet add tests/EpcForwarder.Infrastructure.Tests package Testcontainers.MsSql
dotnet add tests/EpcForwarder.Infrastructure.Tests package Dapper
dotnet add tests/EpcForwarder.Infrastructure.Tests package Microsoft.Data.SqlClient
```
Delete the template `tests/EpcForwarder.Infrastructure.Tests/UnitTest1.cs`.

- [ ] **Step 2: Write the shared SQL Server fixture**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlServerFixture.cs
using EpcForwarder.Infrastructure.Persistence;
using Testcontainers.MsSql;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

/// <summary>テスト全体で1つの SQL Server コンテナを起動し、マイグレーションを適用する。</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        MigrationRunner.Apply(ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;
```

- [ ] **Step 3: Add a tiny smoke test to verify the container + migrations**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/MigrationSmokeTests.cs
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class MigrationSmokeTests(SqlServerFixture fx)
{
    [Fact]
    public void Migration_CreatesExpectedTables()
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        var tables = conn.Query<string>(
            "SELECT name FROM sys.tables WHERE name IN ('tenant','product','session','reading','snapshot')")
            .ToHashSet();

        Assert.Contains("session", tables);
        Assert.Contains("reading", tables);
        Assert.Contains("snapshot", tables);
        Assert.Contains("product", tables);
        Assert.Contains("tenant", tables);
    }
}
```

- [ ] **Step 4: Run the smoke test (starts the container)**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo`
Expected: PASS（初回はSQL Serverイメージ取得で時間がかかる）。Docker未稼働なら失敗するので Docker を起動してから再実行。

- [ ] **Step 5: Commit**

```bash
git add tests/EpcForwarder.Infrastructure.Tests/ EpcForwarder.sln
git commit -m "test(infra): 統合テストプロジェクトとTestcontainers SQL Serverフィクスチャ"
```

---

## Task 4: SqlSessionStore

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Persistence/SqlSessionStore.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlSessionStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlSessionStoreTests.cs
using Dapper;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlSessionStoreTests(SqlServerFixture fx)
{
    private int NewTenant()
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        return conn.QuerySingle<int>(
            "INSERT INTO dbo.tenant(code,name) OUTPUT INSERTED.tenant_id VALUES(@c,'t')",
            new { c = Guid.NewGuid().ToString("N") });
    }

    [Fact]
    public void Get_Unknown_ReturnsNull()
    {
        var store = new SqlSessionStore(new SqlConnectionFactory(fx.ConnectionString));
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Save_Insert_Then_Get_RoundTrips()
    {
        var store = new SqlSessionStore(new SqlConnectionFactory(fx.ConnectionString));
        var id = Guid.NewGuid();
        var s = new Session(id, NewTenant(), SessionType.Inventory, "CAMP-1", DateTimeOffset.UnixEpoch);

        store.Save(s);
        var loaded = store.Get(id)!;

        Assert.Equal(SessionType.Inventory, loaded.Type);
        Assert.Equal("CAMP-1", loaded.BusinessKey);
        Assert.Equal(SessionStatus.Open, loaded.Status);
    }

    [Fact]
    public void Save_Update_PersistsStatusTransition()
    {
        var store = new SqlSessionStore(new SqlConnectionFactory(fx.ConnectionString));
        var id = Guid.NewGuid();
        var s = new Session(id, NewTenant(), SessionType.Shipment, "DN-1", DateTimeOffset.UnixEpoch);
        store.Save(s);

        s.Finalize(DateTimeOffset.UnixEpoch.AddMinutes(1));
        s.MarkForwarded(DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.Save(s); // upsert

        var loaded = store.Get(id)!;
        Assert.Equal(SessionStatus.Forwarded, loaded.Status);
        Assert.NotNull(loaded.FinalizedAt);
        Assert.NotNull(loaded.ForwardedAt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlSessionStoreTests`
Expected: コンパイル失敗（`SqlSessionStore` 未定義）。

- [ ] **Step 3: Implement**

```csharp
// src/EpcForwarder.Infrastructure/Persistence/SqlSessionStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlSessionStore(SqlConnectionFactory factory) : ISessionStore
{
    public Session? Get(Guid publicId)
    {
        using var conn = factory.Create();
        var row = conn.QuerySingleOrDefault<SessionRow>(
            """
            SELECT public_id AS PublicId, tenant_id AS TenantId, type AS Type, business_key AS BusinessKey,
                   status AS Status, expected_count AS ExpectedCount, created_at AS CreatedAt,
                   last_event_at AS LastEventAt, finalized_at AS FinalizedAt, forwarded_at AS ForwardedAt
            FROM dbo.session WHERE public_id = @publicId
            """, new { publicId });

        return row is null
            ? null
            : Session.Rehydrate(
                row.PublicId, row.TenantId, Enum.Parse<SessionType>(row.Type), row.BusinessKey,
                Enum.Parse<SessionStatus>(row.Status), row.ExpectedCount,
                row.CreatedAt, row.LastEventAt, row.FinalizedAt, row.ForwardedAt);
    }

    public void Save(Session session)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            MERGE dbo.session AS t
            USING (SELECT @PublicId AS public_id) AS s ON t.public_id = s.public_id
            WHEN MATCHED THEN UPDATE SET
                status = @Status, expected_count = @ExpectedCount, last_event_at = @LastEventAt,
                finalized_at = @FinalizedAt, forwarded_at = @ForwardedAt
            WHEN NOT MATCHED THEN INSERT
                (public_id, tenant_id, type, business_key, status, expected_count, created_at, last_event_at, finalized_at, forwarded_at)
                VALUES (@PublicId, @TenantId, @Type, @BusinessKey, @Status, @ExpectedCount, @CreatedAt, @LastEventAt, @FinalizedAt, @ForwardedAt);
            """,
            new
            {
                session.PublicId,
                session.TenantId,
                Type = session.Type.ToString(),
                session.BusinessKey,
                Status = session.Status.ToString(),
                session.ExpectedCount,
                CreatedAt = session.CreatedAt,
                LastEventAt = session.LastEventAt,
                FinalizedAt = session.FinalizedAt,
                ForwardedAt = session.ForwardedAt,
            });
    }

    private sealed class SessionRow
    {
        public Guid PublicId { get; init; }
        public int TenantId { get; init; }
        public string Type { get; init; } = "";
        public string? BusinessKey { get; init; }
        public string Status { get; init; } = "";
        public int? ExpectedCount { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastEventAt { get; init; }
        public DateTimeOffset? FinalizedAt { get; init; }
        public DateTimeOffset? ForwardedAt { get; init; }
    }
}
```

> 注: タイムスタンプ列は `DATETIMEOFFSET(3)` としたため、ドメインの `DateTimeOffset` が Dapper で素直に往復する（`DATETIME2`→`DateTime` のキャスト問題を回避）。本タスクの日時アサーションは NotNull/状態に限定し、厳密一致には依存しない。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlSessionStoreTests`
Expected: PASS（3ケース）。日時の往復で型不整合が出たら上記注のとおり調整。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Persistence/SqlSessionStore.cs tests/EpcForwarder.Infrastructure.Tests/SqlSessionStoreTests.cs
git commit -m "feat(infra): SqlSessionStore"
```

---

## Task 5: SqlReadingStore（MERGE後勝ち）

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlReadingStoreTests(SqlServerFixture fx)
{
    private SqlReadingStore Store() => new(new SqlConnectionFactory(fx.ConnectionString));

    [Fact]
    public void Upsert_SameEpc_LastWriteWins()
    {
        var store = Store();
        var sid = Guid.NewGuid();
        store.Upsert(sid, new ReadingEntry("302DB42318A0038000001231", "302DB42318A0038000000000", "devA", DateTimeOffset.UnixEpoch));
        store.Upsert(sid, new ReadingEntry("302DB42318A0038000001231", "302DB42318A0038000000000", "devB", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.Equal(1, store.CountUnique(sid));
        Assert.Equal("devB", store.List(sid).Single().DeviceId);
    }

    [Fact]
    public void List_ReturnsAllUniqueEpcs_ForSession()
    {
        var store = Store();
        var sid = Guid.NewGuid();
        store.Upsert(sid, new ReadingEntry("AA01", "K1", "d", DateTimeOffset.UnixEpoch));
        store.Upsert(sid, new ReadingEntry("AA02", "K1", "d", DateTimeOffset.UnixEpoch));

        Assert.Equal(2, store.CountUnique(sid));
        Assert.Equal(new[] { "AA01", "AA02" }, store.List(sid).Select(r => r.Epc).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Upsert_NullSearchKey_RoundTrips()
    {
        var store = Store();
        var sid = Guid.NewGuid();
        store.Upsert(sid, new ReadingEntry("BB01", null, "d", DateTimeOffset.UnixEpoch));
        Assert.Null(store.List(sid).Single().SearchKey);
    }
}
```
> EPCは hex 文字列。`AA01` は2バイト、`302D...` は12バイト。`Convert.FromHexString` が通る偶数長hexであること。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlReadingStoreTests`
Expected: コンパイル失敗（`SqlReadingStore` 未定義）。

- [ ] **Step 3: Implement**

```csharp
// src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlReadingStore(SqlConnectionFactory factory) : IReadingStore
{
    public void Upsert(Guid sessionId, ReadingEntry entry)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            MERGE dbo.reading WITH (HOLDLOCK) AS t
            USING (SELECT @SessionId AS session_id, @Epc AS epc) AS s
               ON t.session_id = s.session_id AND t.epc = s.epc
            WHEN MATCHED THEN UPDATE SET
               search_key = @SearchKey, device_id = @DeviceId, read_at = @ReadAt,
               updated_at = SYSDATETIMEOFFSET(), excluded = 0
            WHEN NOT MATCHED THEN INSERT
               (session_id, tenant_id, epc, search_key, device_id, read_at)
               VALUES (@SessionId, 0, @Epc, @SearchKey, @DeviceId, @ReadAt);
            """,
            new
            {
                SessionId = sessionId,
                Epc = Convert.FromHexString(entry.Epc),
                SearchKey = entry.SearchKey is null ? null : Convert.FromHexString(entry.SearchKey),
                entry.DeviceId,
                ReadAt = entry.ReadAt,
            });
    }

    public IReadOnlyList<ReadingEntry> List(Guid sessionId)
    {
        using var conn = factory.Create();
        var rows = conn.Query<ReadingRow>(
            """
            SELECT epc AS Epc, search_key AS SearchKey, device_id AS DeviceId, read_at AS ReadAt
            FROM dbo.reading WHERE session_id = @sessionId AND excluded = 0
            """, new { sessionId });

        return rows.Select(r => new ReadingEntry(
            Convert.ToHexString(r.Epc),
            r.SearchKey is null ? null : Convert.ToHexString(r.SearchKey),
            r.DeviceId,
            r.ReadAt)).ToList();
    }

    public int CountUnique(Guid sessionId)
    {
        using var conn = factory.Create();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.reading WHERE session_id = @sessionId AND excluded = 0",
            new { sessionId });
    }

    private sealed class ReadingRow
    {
        public byte[] Epc { get; init; } = [];
        public byte[]? SearchKey { get; init; }
        public string? DeviceId { get; init; }
        public DateTimeOffset ReadAt { get; init; }
    }
}
```
> `tenant_id` は本ストアでは未使用のため 0 を入れる（テナントは session 経由で解決する設計。reading 単独でのテナント絞りは将来）。`UQ_reading(session_id, epc)` で後勝ちを保証。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlReadingStoreTests`
Expected: PASS（3ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreTests.cs
git commit -m "feat(infra): SqlReadingStore(MERGE後勝ち)"
```

---

## Task 6: SqlSnapshotStore

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Persistence/SqlSnapshotStore.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlSnapshotStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlSnapshotStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlSnapshotStoreTests(SqlServerFixture fx)
{
    private SqlSnapshotStore Store() => new(new SqlConnectionFactory(fx.ConnectionString));

    [Fact]
    public void NextVersion_IncrementsPerSession()
    {
        var store = Store();
        var sid = Guid.NewGuid();

        var v1 = store.NextVersion(sid);
        store.Record(new SnapshotRecord(sid, v1, false, Guid.NewGuid(), 3, true));
        var v2 = store.NextVersion(sid);

        Assert.Equal(1, v1);
        Assert.Equal(2, v2);
    }

    [Fact]
    public void Record_Persists_AndIsScopedPerSession()
    {
        var store = Store();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        store.Record(new SnapshotRecord(a, store.NextVersion(a), true, Guid.NewGuid(), 1, true));

        Assert.Equal(1, store.NextVersion(b)); // 別セッションは1から
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlSnapshotStoreTests`
Expected: コンパイル失敗（`SqlSnapshotStore` 未定義）。

- [ ] **Step 3: Implement**

```csharp
// src/EpcForwarder.Infrastructure/Persistence/SqlSnapshotStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlSnapshotStore(SqlConnectionFactory factory) : ISnapshotStore
{
    // PoC: MAX(version)+1 のベストエフォート(セッション単位=単一ライタ想定)。競合は UQ_snapshot_ver が検出。
    public int NextVersion(Guid sessionId)
    {
        using var conn = factory.Create();
        return conn.ExecuteScalar<int>(
            "SELECT ISNULL(MAX(version),0)+1 FROM dbo.snapshot WHERE session_id = @sessionId",
            new { sessionId });
    }

    public void Record(SnapshotRecord record)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            INSERT INTO dbo.snapshot (tenant_id, session_id, version, is_final, idempotency_key, item_count, success)
            VALUES (0, @SessionId, @Version, @IsFinal, @IdempotencyKey, @ItemCount, @Success)
            """,
            new
            {
                record.SessionId,
                record.Version,
                record.IsFinal,
                record.IdempotencyKey,
                record.ItemCount,
                record.Success,
            });
    }
}
```
> `tenant_id` は 0 固定（snapshot のテナント絞りは設定読み込みサブ計画で session 結合に置換）。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlSnapshotStoreTests`
Expected: PASS（2ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Persistence/SqlSnapshotStore.cs tests/EpcForwarder.Infrastructure.Tests/SqlSnapshotStoreTests.cs
git commit -m "feat(infra): SqlSnapshotStore"
```

---

## Task 7: SqlProductStore（読み書き）＋ DI拡張

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Persistence/SqlProductStore.cs`
- Create: `src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlProductStoreTests.cs`

DI拡張のため Infrastructure に `Microsoft.Extensions.DependencyInjection.Abstractions` が要る。

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlProductStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Dapper;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlProductStoreTests(SqlServerFixture fx)
{
    private int NewTenant()
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        return conn.QuerySingle<int>(
            "INSERT INTO dbo.tenant(code,name) OUTPUT INSERTED.tenant_id VALUES(@c,'t')",
            new { c = Guid.NewGuid().ToString("N") });
    }

    [Fact]
    public void Upsert_ThenResolveSku()
    {
        var store = new SqlProductStore(new SqlConnectionFactory(fx.ConnectionString));
        var tenant = NewTenant();
        store.Upsert(new ProductRecord(tenant, "302DB42318A0038000000000", "ITEM-AAA", "ST-100", "BLK", "M", null));

        Assert.Equal("ITEM-AAA", store.ResolveSku(tenant, "302DB42318A0038000000000"));
        Assert.Null(store.ResolveSku(tenant, "FFFFFFFFFFFFFFFFFFFFFFFF"));
    }

    [Fact]
    public void Upsert_SameKey_Overwrites()
    {
        var store = new SqlProductStore(new SqlConnectionFactory(fx.ConnectionString));
        var tenant = NewTenant();
        store.Upsert(new ProductRecord(tenant, "AABB", "OLD", null, null, null, null));
        store.Upsert(new ProductRecord(tenant, "AABB", "NEW", null, null, null, null));

        Assert.Equal("NEW", store.ResolveSku(tenant, "AABB"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlProductStoreTests`
Expected: コンパイル失敗（`SqlProductStore` 未定義）。

- [ ] **Step 3: Implement store and DI extension**

```csharp
// src/EpcForwarder.Infrastructure/Persistence/SqlProductStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlProductStore(SqlConnectionFactory factory) : IProductCatalog, IProductWriteStore
{
    public string? ResolveSku(int tenantId, string searchKey)
    {
        using var conn = factory.Create();
        return conn.QuerySingleOrDefault<string>(
            "SELECT sku FROM dbo.product WHERE tenant_id = @tenantId AND search_key = @key",
            new { tenantId, key = Convert.FromHexString(searchKey) });
    }

    public void Upsert(ProductRecord product)
    {
        using var conn = factory.Create();
        conn.Execute(
            """
            MERGE dbo.product AS t
            USING (SELECT @TenantId AS tenant_id, @Key AS search_key) AS s
               ON t.tenant_id = s.tenant_id AND t.search_key = s.search_key
            WHEN MATCHED THEN UPDATE SET
               sku = @Sku, item_code = @ItemCode, color = @Color, size = @Size,
               description = @Description, updated_at = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN INSERT
               (tenant_id, search_key, sku, item_code, color, size, description)
               VALUES (@TenantId, @Key, @Sku, @ItemCode, @Color, @Size, @Description);
            """,
            new
            {
                product.TenantId,
                Key = Convert.FromHexString(product.SearchKey),
                product.Sku,
                product.ItemCode,
                product.Color,
                product.Size,
                product.Description,
            });
    }
}
```

```csharp
// src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs
using EpcForwarder.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EpcForwarder.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>Azure SQL 永続化(4ポート)を登録する。</summary>
    public static IServiceCollection AddSqlPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(new SqlConnectionFactory(connectionString));
        services.AddSingleton<ISessionStore, SqlSessionStore>();
        services.AddSingleton<IReadingStore, SqlReadingStore>();
        services.AddSingleton<ISnapshotStore, SqlSnapshotStore>();
        services.AddSingleton<SqlProductStore>();
        services.AddSingleton<IProductCatalog>(sp => sp.GetRequiredService<SqlProductStore>());
        services.AddSingleton<IProductWriteStore>(sp => sp.GetRequiredService<SqlProductStore>());
        return services;
    }
}
```
Run (to add the DI package):
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add src/EpcForwarder.Infrastructure package Microsoft.Extensions.DependencyInjection.Abstractions
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlProductStoreTests`
Expected: PASS（2ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Persistence/SqlProductStore.cs src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj tests/EpcForwarder.Infrastructure.Tests/SqlProductStoreTests.cs
git commit -m "feat(infra): SqlProductStore とDI登録(AddSqlPersistence)"
```

---

## Task 8: 永続化版エンドツーエンド統合テスト

ドメインサービス（`ReadingIngestor` / `InventoryDeliverer` / `SnapshotPublisher` / `ProductRegistrar`）を **SQLストア実装**に差し、棚卸の一連（登録→取込後勝ち→仮確定→確定）がSQL越しに成立することを1本で検証する。Webhook送信は `CapturingWebhookSender` 相当の最小スタブを内製（テスト内に実装）し、HTTPは介さない（HTTP送信は既存スライスで検証済み）。

**Files:**
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlBackedInventoryFlowTests.cs`

- [ ] **Step 1: Write the test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlBackedInventoryFlowTests.cs
using Dapper;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlBackedInventoryFlowTests(SqlServerFixture fx)
{
    private sealed class StubSender : IWebhookSender
    {
        public List<WebhookRequest> Requests { get; } = new();
        public Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new WebhookResult(true, 200));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class SeqIds : IIdGenerator { private int _n; public Guid NewGuid() => new($"00000000-0000-0000-0000-{(++_n):D12}"); }
    private sealed class NoSecrets : ISecretStore { public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null); }

    private int NewTenant()
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        return conn.QuerySingle<int>(
            "INSERT INTO dbo.tenant(code,name) OUTPUT INSERTED.tenant_id VALUES(@c,'t')",
            new { c = Guid.NewGuid().ToString("N") });
    }

    [Fact]
    public async Task Inventory_OverSql_Register_Ingest_Provisional_Finalize()
    {
        var cf = new SqlConnectionFactory(fx.ConnectionString);
        var sessions = new SqlSessionStore(cf);
        var readings = new SqlReadingStore(cf);
        var snaps = new SqlSnapshotStore(cf);
        var products = new SqlProductStore(cf);
        var sender = new StubSender();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SeqIds();

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var publisher = new SnapshotPublisher(readings, products, snaps, sender, new NoSecrets(), new PayloadBuilder(), clock, ids);
        var inventory = new InventoryDeliverer(sessions, publisher, clock);
        var registrar = new ProductRegistrar(products);

        var tenant = NewTenant();
        var gtin = GtinTestData.Gtin14("0000000000001"); // 共有ヘルパ(Core.Tests)と同じ規則
        var key = registrar.Register(tenant, gtin, gcpLength: 7, filter: 1, sku: "ITEM-AAA");

        var sid = Guid.NewGuid();
        sessions.Save(new Session(sid, tenant, SessionType.Inventory, "CAMP-1", clock.UtcNow));

        // 実タグ(同一商品・別シリアル)。登録キーにシリアルを載せたEPC。
        var tagBytes = Convert.FromHexString(key); tagBytes[11] = 0x2A;
        var epc1 = Convert.ToHexString(tagBytes);
        tagBytes[11] = 0x2B; var epc2 = Convert.ToHexString(tagBytes);

        ingestor.Ingest(sid, epc1, "devA", clock.UtcNow, resolveSku: true);
        ingestor.Ingest(sid, epc1, "devB", clock.UtcNow, resolveSku: true); // 後勝ち→1件
        ingestor.Ingest(sid, epc2, "devA", clock.UtcNow, resolveSku: true);

        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null, new Dictionary<string, string>());

        await inventory.SendProvisionalAsync(sid, target);
        Assert.Equal(SessionStatus.Open, sessions.Get(sid)!.Status);

        var final = await inventory.FinalizeAndDeliverAsync(sid, target);

        Assert.True(final.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(sid)!.Status);
        Assert.Equal(2, sender.Requests.Count);
        Assert.Contains("\"is_final\":false", sender.Requests[0].Body);
        Assert.Contains("\"is_final\":true", sender.Requests[1].Body);
        Assert.Contains("\"sku\":\"ITEM-AAA\"", sender.Requests[1].Body);
        Assert.Contains("\"quantity\":2", sender.Requests[1].Body); // epc1+epc2 が同一SKUに集約
    }
}
```
> `GtinTestData` は Core.Tests の test 用ヘルパ。Infrastructure.Tests から参照できないため、**この計画では `GtinTestData` 相当の小ヘルパをこのテストファイル内に再掲する**（チェックディジット付与の8行）。実装時に同等のローカル関数を置くこと。

- [ ] **Step 2: Add the local GTIN helper inside the test**

`GtinTestData.Gtin14(...)` 呼び出しを、テストクラス内の private static `Gtin14(string body13)`（GTIN計画と同一のmod-10付与・8行）へ置換する。

- [ ] **Step 3: Run the test**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlBackedInventoryFlowTests`
Expected: PASS。

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: Core.Tests 全緑 ＋ Infrastructure.Tests 全緑（Docker稼働下）。

- [ ] **Step 5: Commit**

```bash
git add tests/EpcForwarder.Infrastructure.Tests/SqlBackedInventoryFlowTests.cs
git commit -m "test(infra): SQL永続化越しの棚卸フロー(登録→取込→仮確定→確定)"
```

---

## 完了条件

- `dotnet build` 0警告/0エラー。`dotnet test EpcForwarder.sln` が Core/Infrastructure とも緑（Docker稼働）。
- 4つの永続化ポートが Azure SQL（=ローカルSQL Server）実装で in-memory フェイクと同じ契約を満たし、ドメインサービスをSQLストアに差してフローが成立する。
- 後続サブ計画: ② Key Vault `ISecretStore`(TTLキャッシュ) ＋ `IDeviceFeedback`(C2D) ＋ `IHostResolver`、③ Functions host（IoT Hubトリガー `Ingestion` ＋ HTTP API `Api`、`AddSqlPersistence` でDI結線）、④ Bicep（IoT Hub/Functions/SQL/Key Vault/Storage/App Insights）と実機デプロイ手順書 → 実機E2E。
