# Azureアダプタ②: 宛先設定のSQL読み込み Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 宛先（Webhook連携先）設定をDBから読み込み、テナント単位で配信に使う `DeliveryTarget` を組み立てられるようにする。これにより③b Functions host の配信エンドポイントが、リクエストボディではなく**設定（DB）**から正しく宛先を解決できる。

**Architecture:** `destination` / `destination_header` / `mask` テーブルを追加（data-model.md §3.2/§4 準拠）。`IDestinationCatalog`（Core.Abstractions）を新設し、`SqlDestinationStore`（Infrastructure/Persistence）が `destination` ＋ `destination_header` を結合して `DeliveryTarget`（既存・Core.Delivery）のリストをテナント単位で返す。Docker 上の SQL Server コンテナで統合テスト。

**Tech Stack:** C# / .NET 8、Dapper、Microsoft.Data.SqlClient、xUnit、Testcontainers.MsSql、Docker。

**Scope boundary:**
- 含む: マイグレーション `0002_destinations.sql`（destination/destination_header/mask）、`IDestinationCatalog`＋`SqlDestinationStore`（DB→`DeliveryTarget`）、`AddSqlPersistence` 登録、Testcontainers 統合テスト。
- 含まない（後続）: `mask` の読み込み実体（v1パイプラインは固定SGTIN-96マスク＝`EpcKey.Sgtin96Mask` を使用。マルチマスク・ルーティングは将来。`mask` テーブルはスキーマ完成のため作成のみ）。`payload_mode`/`allow_provisional` 列は作成・保持するが、現 `DeliveryTarget`/deliverers がまだ消費しないため読み込みは将来（モード対応時）。宛先の管理用書き込みAPI（登録/更新）は③b/管理画面側。
- **検証範囲（合意済み）**: ローカル（Dockerコンテナ）統合テストまで。

**Prerequisites:** `dotnet` は ~/.dotnet。各コマンド前に `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH`。Docker 稼働必須。全テスト: `dotnet test EpcForwarder.sln --nologo`。`Directory.Build.props` は `TreatWarningsAsErrors=true`。

**メモ:** 既存 csproj の埋め込みは `db\migrations\*.sql` のワイルドカードなので、`0002_*.sql` は自動的に埋め込まれ、`MigrationRunner` が名前順（0001→0002）で適用する（csproj変更不要）。マイグレーションは `IF OBJECT_ID(...) IS NULL` で冪等。

---

## File Structure

| ファイル | 責務 |
|---|---|
| `db/migrations/0002_destinations.sql`（新規） | destination / destination_header / mask テーブル |
| `src/EpcForwarder.Core/Abstractions/Ports.cs`（変更） | `IDestinationCatalog` 追加 |
| `src/EpcForwarder.Infrastructure/Persistence/SqlDestinationStore.cs`（新規） | DB→`DeliveryTarget` リスト |
| `src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs`（変更） | `AddSqlPersistence` に登録追加 |
| `tests/EpcForwarder.Infrastructure.Tests/MigrationSmokeTests.cs`（変更） | 追加テーブルの存在確認 |
| `tests/EpcForwarder.Infrastructure.Tests/SqlDestinationStoreTests.cs`（新規） | DeliveryTarget組み立ての統合テスト |
| `tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs`（変更） | `IDestinationCatalog` のDI解決を追加 |

---

## Task 1: マイグレーション 0002（destination / destination_header / mask）

**Files:**
- Create: `db/migrations/0002_destinations.sql`
- Modify: `tests/EpcForwarder.Infrastructure.Tests/MigrationSmokeTests.cs`

- [ ] **Step 1: Write the migration**

```sql
-- 0002_destinations.sql  正本: docs/design/data-model.md §3.2/§4
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
```

- [ ] **Step 2: Extend the migration smoke test**

`MigrationSmokeTests.cs` の `IN (...)` リストと Assert に新テーブルを追加する。テスト本体を次へ置換:

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
            "SELECT name FROM sys.tables WHERE name IN ('tenant','product','session','reading','snapshot','destination','destination_header','mask')")
            .ToHashSet();

        Assert.Contains("session", tables);
        Assert.Contains("reading", tables);
        Assert.Contains("snapshot", tables);
        Assert.Contains("product", tables);
        Assert.Contains("tenant", tables);
        Assert.Contains("destination", tables);
        Assert.Contains("destination_header", tables);
        Assert.Contains("mask", tables);
    }
}
```

- [ ] **Step 3: Run the smoke test**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~MigrationSmokeTests`
Expected: PASS（コンテナ起動＋0001/0002適用で8テーブル）。

- [ ] **Step 4: Commit**

```bash
git add db/migrations/0002_destinations.sql tests/EpcForwarder.Infrastructure.Tests/MigrationSmokeTests.cs
git commit -m "feat(infra): マイグレーション0002 destination/destination_header/mask"
```

---

## Task 2: IDestinationCatalog ＋ SqlDestinationStore

**Files:**
- Modify: `src/EpcForwarder.Core/Abstractions/Ports.cs`（`IDestinationCatalog` 追加）
- Create: `src/EpcForwarder.Infrastructure/Persistence/SqlDestinationStore.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlDestinationStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlDestinationStoreTests.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlDestinationStoreTests(SqlServerFixture fx)
{
    private int InsertDestination(int tenantId, string url, bool active, bool hmac, string? hmacRef)
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        return conn.QuerySingle<int>(
            """
            INSERT INTO dbo.destination (tenant_id, name, url, schema_version, hmac_enabled, hmac_secret_ref, is_active)
            OUTPUT INSERTED.destination_id
            VALUES (@tenantId, 'd', @url, '1', @hmac, @hmacRef, @active)
            """,
            new { tenantId, url, hmac, hmacRef, active });
    }

    private void InsertHeader(int destId, string name, string valueRef)
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        conn.Execute(
            "INSERT INTO dbo.destination_header (destination_id, header_name, value_ref) VALUES (@destId, @name, @valueRef)",
            new { destId, name, valueRef });
    }

    [Fact]
    public void GetActiveTargets_BuildsDeliveryTarget_WithHeaders()
    {
        var tenant = fx.NewTenant();
        var id = InsertDestination(tenant, "https://api.example.com/hook", active: true, hmac: true, hmacRef: "hook-hmac");
        InsertHeader(id, "Authorization", "auth-secret");
        InsertHeader(id, "X-API-KEY", "apikey-secret");

        var targets = new SqlDestinationStore(new SqlConnectionFactory(fx.ConnectionString)).GetActiveTargets(tenant);

        var t = Assert.Single(targets);
        Assert.Equal("https://api.example.com/hook", t.Url);
        Assert.Equal("POST", t.Method);
        Assert.Equal("1", t.SchemaVersion);
        Assert.True(t.HmacEnabled);
        Assert.Equal("hook-hmac", t.HmacSecretRef);
        Assert.Equal("auth-secret", t.Headers["Authorization"]);
        Assert.Equal("apikey-secret", t.Headers["X-API-KEY"]);
    }

    [Fact]
    public void GetActiveTargets_ExcludesInactive_AndOtherTenants()
    {
        var tenant = fx.NewTenant();
        var other = fx.NewTenant();
        InsertDestination(tenant, "https://active.example.com", active: true, hmac: false, hmacRef: null);
        InsertDestination(tenant, "https://inactive.example.com", active: false, hmac: false, hmacRef: null);
        InsertDestination(other, "https://other.example.com", active: true, hmac: false, hmacRef: null);

        var targets = new SqlDestinationStore(new SqlConnectionFactory(fx.ConnectionString)).GetActiveTargets(tenant);

        var t = Assert.Single(targets);
        Assert.Equal("https://active.example.com", t.Url);
    }

    [Fact]
    public void GetActiveTargets_NoDestinations_ReturnsEmpty()
    {
        var tenant = fx.NewTenant();
        Assert.Empty(new SqlDestinationStore(new SqlConnectionFactory(fx.ConnectionString)).GetActiveTargets(tenant));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlDestinationStoreTests`
Expected: コンパイル失敗（`IDestinationCatalog`/`SqlDestinationStore` 未定義）。

- [ ] **Step 3: Add the port and implement the store**

`src/EpcForwarder.Core/Abstractions/Ports.cs` の末尾に追記:

```csharp
public interface IDestinationCatalog
{
    /// <summary>テナントの有効な配信先を DeliveryTarget として返す。</summary>
    IReadOnlyList<EpcForwarder.Core.Delivery.DeliveryTarget> GetActiveTargets(int tenantId);
}
```

```csharp
// src/EpcForwarder.Infrastructure/Persistence/SqlDestinationStore.cs
using Dapper;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;

namespace EpcForwarder.Infrastructure.Persistence;

public sealed class SqlDestinationStore(SqlConnectionFactory factory) : IDestinationCatalog
{
    public IReadOnlyList<DeliveryTarget> GetActiveTargets(int tenantId)
    {
        using var conn = factory.Create();
        var destinations = conn.Query<DestinationRow>(
            """
            SELECT destination_id AS Id, url AS Url, http_method AS Method, schema_version AS SchemaVersion,
                   hmac_enabled AS HmacEnabled, hmac_secret_ref AS HmacSecretRef
            FROM dbo.destination WHERE tenant_id = @tenantId AND is_active = 1
            """, new { tenantId }).ToList();

        var targets = new List<DeliveryTarget>(destinations.Count);
        foreach (var d in destinations)
        {
            var headers = conn.Query<HeaderRow>(
                "SELECT header_name AS Name, value_ref AS ValueRef FROM dbo.destination_header WHERE destination_id = @id",
                new { id = d.Id })
                .ToDictionary(h => h.Name, h => h.ValueRef);

            targets.Add(new DeliveryTarget(d.Url, d.Method, d.SchemaVersion, d.HmacEnabled, d.HmacSecretRef, headers));
        }

        return targets;
    }

    private sealed class DestinationRow
    {
        public int Id { get; init; }
        public string Url { get; init; } = "";
        public string Method { get; init; } = "";
        public string SchemaVersion { get; init; } = "";
        public bool HmacEnabled { get; init; }
        public string? HmacSecretRef { get; init; }
    }

    private sealed class HeaderRow
    {
        public string Name { get; init; } = "";
        public string ValueRef { get; init; } = "";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~SqlDestinationStoreTests`
Expected: PASS（3ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Abstractions/Ports.cs src/EpcForwarder.Infrastructure/Persistence/SqlDestinationStore.cs tests/EpcForwarder.Infrastructure.Tests/SqlDestinationStoreTests.cs
git commit -m "feat(infra): IDestinationCatalog と SqlDestinationStore(DB→DeliveryTarget)"
```

---

## Task 3: DI登録 ＋ 解決テスト

**Files:**
- Modify: `src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs`（`AddSqlPersistence` に追加）
- Modify: `tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs`

- [ ] **Step 1: Register the catalog**

`AddSqlPersistence` の登録群に追記（`return services;` の直前）:

```csharp
        services.AddSingleton<IDestinationCatalog, SqlDestinationStore>();
```
`using EpcForwarder.Core.Abstractions;` は既にあるはず。無ければ追加。

- [ ] **Step 2: Add a resolution assertion**

`AddEpcForwarderTests.cs` の `[Theory]` の `[InlineData]` 群に1行追加:

```csharp
    [InlineData(typeof(EpcForwarder.Core.Abstractions.IDestinationCatalog))]
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~AddEpcForwarderTests`
Expected: PASS（`IDestinationCatalog` 含む全解決ケース）。

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: Core.Tests 全緑 ＋ Infrastructure.Tests 全緑（Docker稼働下）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs
git commit -m "feat(infra): IDestinationCatalog をDI登録し解決テストに追加"
```

---

## 完了条件

- `dotnet build` 0警告/0エラー。`dotnet test EpcForwarder.sln` 全緑（Docker稼働下でSQL統合も）。
- テナント単位で `IDestinationCatalog.GetActiveTargets(tenantId)` が DB から `DeliveryTarget`（URL/メソッド/schema_version/HMAC設定/ヘッダ参照）を組み立てて返す。有効・テナントで正しく絞られる。
- 後続: ③b Functions host が `IDestinationCatalog` で宛先を解決して配信エンドポイントを実装（リクエストボディに宛先を載せない）。`payload_mode`/`allow_provisional`/`mask` の消費は各モード対応時に追加。④ Bicep＋実機デプロイ手順書 → 実機E2E。
