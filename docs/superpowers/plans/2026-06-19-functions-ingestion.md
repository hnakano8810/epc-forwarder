# ③b-1 Functions host 結線 ＋ Ingestion(書き込み経路) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** IoT Hub(内蔵Event Hubs互換エンドポイント)からの読取/完了メッセージを Azure Functions で受け、遅延セッション生成→読取取込→(伝票)完了で到達性突合→一致時に宛先解決して自動配信するまでの書き込み経路を結線する。

**Architecture:** Core に取込オーケストレータ `IngestionDispatcher` を追加(遅延セッション生成・読取取込・完了時の reconcile→自動配信)。Functions 側は EventHub トリガーで受けた JSON バッチをパースして Dispatcher へ流す薄いホスト。配信先は既存 `IDestinationCatalog` で解決。ロケーション情報を `ReadingEntry` ポートに通し、reading の tenant_id ハードコード(=0)バグを session からのサブクエリで解消する。`HttpWebhookSender` を `IHttpClientFactory` 化して Functions ホストの推奨パターンに合わせる。

**Tech Stack:** .NET 8, Azure Functions Worker(isolated, V4), Microsoft.Azure.Functions.Worker.Extensions.EventHubs, Dapper + Microsoft.Data.SqlClient(同期), xUnit, Testcontainers.MsSql。

**検証方針(重要):** 私(実装者)はユーザーのAzureにデプロイ不可。EventHub トリガーのバインディング自体はローカル実行不可のため**ビルド検証のみ**。ロジック(Dispatcher・JSONパーサ・SQL永続化)はローカル単体＋Dockerコンテナ統合で緑にする。

**前提コマンド(dotnet):** 各 `dotnet` 実行の前に必ず:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
```

**制約(維持):**
- `Directory.Build.props` の `TreatWarningsAsErrors=true` を維持(警告ゼロで通すこと)。
- `Microsoft.Extensions.*` は 10.x(Functions Worker SDK 依存に整合。8.x へ下げない)。新規パッケージは `dotnet add package`(明示バージョン指定なし)で互換最新を取得。
- SQLアクセスは Dapper + Microsoft.Data.SqlClient(同期)。

---

## File Structure

**Core(EpcForwarder.Core)**
- Modify `src/EpcForwarder.Core/Abstractions/Ports.cs` — `ReadingEntry` に `ReadLocation? Location` を追加、`ReadLocation` レコード新設。
- Modify `src/EpcForwarder.Core/Sessions/ReadingIngestor.cs` — `Ingest` に任意 `ReadLocation? location` 引数を追加。
- Create `src/EpcForwarder.Core/Ingestion/IngestionCommands.cs` — `IIngestionCommand`/`ReadCommand`/`CompleteCommand`/`CompletionOutcome`。
- Create `src/EpcForwarder.Core/Ingestion/IngestionDispatcher.cs` — 遅延生成・読取取込・完了→reconcile→自動配信。

**Infrastructure(EpcForwarder.Infrastructure)**
- Modify `src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs` — location 列の読み書き、tenant_id を session からサブクエリ。
- Modify `src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs` — `IHttpClientFactory` 化。
- Modify `src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` — `AddHttpClient` 化、`IngestionDispatcher` 登録、TODO(HttpClient singleton)除去。
- Modify `src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj` — `Microsoft.Extensions.Http` 追加。

**Functions(EpcForwarder.Functions)**
- Create `src/EpcForwarder.Functions/Ingestion/IngestionMessages.cs` — JSON DTO(wire形式)。
- Create `src/EpcForwarder.Functions/Ingestion/IngestionMessageParser.cs` — JSON → `IIngestionCommand` マッパ。
- Create `src/EpcForwarder.Functions/Ingestion/IngestionFunction.cs` — EventHub トリガー(薄いホスト)。
- Modify `src/EpcForwarder.Functions/Program.cs` — `AddEpcForwarder` を構成から結線。
- Modify `src/EpcForwarder.Functions/EpcForwarder.Functions.csproj` — EventHubs Worker 拡張追加。
- Modify `src/EpcForwarder.Functions/local.settings.json` — ローカル設定キー追加。

**Tests**
- Modify `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs` — `FakeDestinationCatalog` 追加。
- Modify `tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs` — location 伝播テスト。
- Create `tests/EpcForwarder.Core.Tests/Ingestion/IngestionDispatcherTests.cs` — Dispatcher 単体。
- Modify `tests/EpcForwarder.Core.Tests/Infrastructure/HttpWebhookSenderTests.cs` — factory 化対応。
- Modify `tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs` — factory 化対応。
- Modify `tests/EpcForwarder.Infrastructure.Tests/SqlDestinationStoreTests.cs` 隣に `SqlReadingStoreLocationTests.cs` を新設(下記 Task 2)。
- Create `tests/EpcForwarder.Functions.Tests/` プロジェクト — パーサ単体テスト。
- Modify `EpcForwarder.sln` — Functions.Tests を追加。

---

## Task 1: 読取ポートに location を追加(Core)

**Files:**
- Modify: `src/EpcForwarder.Core/Abstractions/Ports.cs`
- Modify: `src/EpcForwarder.Core/Sessions/ReadingIngestor.cs`
- Test: `tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs` に追記(クラス内に新メソッド):

```csharp
    [Fact]
    public void Ingest_WithLocation_StoresLocationOnEntry()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "INV-1", clock.UtcNow));

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var loc = new ReadLocation("TOKYO-DC", "2F", "A-01");

        ingestor.Ingest(id, "302DB42318A0038000001231", "devA", clock.UtcNow, resolveSku: false, location: loc);

        var stored = Assert.Single(readings.List(id));
        Assert.Equal(loc, stored.Location);
    }
```

ファイル先頭の `using` に `EpcForwarder.Core.Abstractions;` が無ければ追加(`ReadLocation` 参照のため)。

- [ ] **Step 2: 失敗を確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet build tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj`
Expected: コンパイルエラー(`ReadLocation` 未定義 / `Ingest` に `location` 引数なし)。

- [ ] **Step 3: `Ports.cs` に `ReadLocation` と `ReadingEntry.Location` を追加**

`src/EpcForwarder.Core/Abstractions/Ports.cs` の `ReadingEntry` 定義を置換:

```csharp
/// <summary>読取に付随するロケーション文脈(棚卸のロケ別集計に使用)。全要素任意。</summary>
public sealed record ReadLocation(string? L1, string? L2, string? L3);

/// <summary>1セッション内の読取1件。EPC単位で後勝ち。</summary>
public sealed record ReadingEntry(string Epc, string? SearchKey, string? DeviceId, DateTimeOffset ReadAt, ReadLocation? Location = null);
```

- [ ] **Step 4: `ReadingIngestor.Ingest` に location 引数を追加**

`src/EpcForwarder.Core/Sessions/ReadingIngestor.cs` の `Ingest` メソッドを置換:

```csharp
    public void Ingest(Guid sessionId, string epcHex, string? deviceId, DateTimeOffset readAt, bool resolveSku, ReadLocation? location = null)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        var searchKey = resolveSku ? Sgtin96.DeriveSearchKey(epcHex) : null;
        readings.Upsert(sessionId, new ReadingEntry(epcHex, searchKey, deviceId, readAt, location));

        session.Touch(clock.UtcNow);
        sessions.Save(session);
    }
```

- [ ] **Step 5: テストが通ることを確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj --filter "FullyQualifiedName~ReadingIngestorTests"`
Expected: PASS(既存 ReadingIngestor テスト含め全緑)。

- [ ] **Step 6: コミット**

```bash
git add src/EpcForwarder.Core/Abstractions/Ports.cs src/EpcForwarder.Core/Sessions/ReadingIngestor.cs tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs
git commit -m "feat(core): ReadingEntry に ReadLocation を追加し Ingest で受け渡し"
```

---

## Task 2: location 永続化 + tenant_id バグ修正(Infrastructure SQL)

`SqlReadingStore` は INSERT 時に `tenant_id` を `0` でハードコードしている(`TODO(poc)`)。session は読取より先に存在する(遅延生成は Task 4 でセッションを先に Save する)ため、`tenant_id` を `dbo.session` からサブクエリで取得して修正する。あわせて location 3列を読み書きする。

**Files:**
- Modify: `src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreLocationTests.cs`

- [ ] **Step 1: 失敗する統合テストを書く**

`tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreLocationTests.cs` を新規作成。既存 `SqlDestinationStoreTests.cs` のフィクスチャ利用パターンに合わせること(同ディレクトリの既存テストで使われている Testcontainers フィクスチャ/接続文字列取得方法・collection 属性をそのまま踏襲する。実装前に `SqlDestinationStoreTests.cs` を読んで同じ書式に合わせる):

```csharp
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Persistence;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

// NOTE: 既存 SqlDestinationStoreTests と同じ collection/フィクスチャに属させ、
// マイグレーション適用済みの接続文字列を同じ方法で取得すること。
[Collection("Sql")]
public class SqlReadingStoreLocationTests
{
    private readonly string _connectionString;

    public SqlReadingStoreLocationTests(SqlServerFixture fixture) // ← 既存フィクスチャ型名に合わせる
    {
        _connectionString = fixture.ConnectionString;
    }

    [Fact]
    public void Upsert_PersistsLocation_And_DerivesTenantFromSession()
    {
        var factory = new SqlConnectionFactory(_connectionString);
        var sessions = new SqlSessionStore(factory);
        var readings = new SqlReadingStore(factory);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UnixEpoch;
        sessions.Save(new Session(id, 42, SessionType.Inventory, "INV-LOC", now));

        var loc = new ReadLocation("TOKYO-DC", "2F", "A-01");
        readings.Upsert(id, new ReadingEntry("302DB42318A0038000001231", null, "devA", now, loc));

        var stored = Assert.Single(readings.List(id));
        Assert.Equal(loc, stored.Location);

        // tenant_id が session(=42)由来であること(ハードコード0でない)
        using var conn = factory.Create();
        var tenant = Dapper.SqlMapper.ExecuteScalar<int>(conn,
            "SELECT tenant_id FROM dbo.reading WHERE session_id = @id", new { id });
        Assert.Equal(42, tenant);
    }
}
```

> フィクスチャ型名/collection 名が異なる場合は `SqlDestinationStoreTests.cs` の宣言に厳密に合わせること(本テストの目的はクエリ検証であり、フィクスチャ配線の発明ではない)。

- [ ] **Step 2: 失敗を確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SqlReadingStoreLocationTests"`
Expected: FAIL(`Location` が null で返る / tenant_id が 0)。Docker 稼働が前提。

- [ ] **Step 3: `SqlReadingStore` の Upsert を修正**

`src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs` の `Upsert` を置換(tenant_id サブクエリ + location 列、`TODO(poc)` コメント削除):

```csharp
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
               location_l1 = @L1, location_l2 = @L2, location_l3 = @L3,
               updated_at = SYSDATETIMEOFFSET(), excluded = 0
            WHEN NOT MATCHED THEN INSERT
               (session_id, tenant_id, epc, search_key, device_id, read_at, location_l1, location_l2, location_l3)
               VALUES (@SessionId,
                       (SELECT tenant_id FROM dbo.session WHERE public_id = @SessionId),
                       @Epc, @SearchKey, @DeviceId, @ReadAt, @L1, @L2, @L3);
            """,
            new
            {
                SessionId = sessionId,
                Epc = Convert.FromHexString(entry.Epc),
                SearchKey = entry.SearchKey is null ? null : Convert.FromHexString(entry.SearchKey),
                entry.DeviceId,
                ReadAt = entry.ReadAt,
                L1 = entry.Location?.L1,
                L2 = entry.Location?.L2,
                L3 = entry.Location?.L3,
            });
    }
```

- [ ] **Step 4: `SqlReadingStore` の List と ReadingRow を修正**

同ファイルの `List` クエリと `ReadingRow` を置換(location 列を select してマップ):

```csharp
    public IReadOnlyList<ReadingEntry> List(Guid sessionId)
    {
        using var conn = factory.Create();
        var rows = conn.Query<ReadingRow>(
            """
            SELECT epc AS Epc, search_key AS SearchKey, device_id AS DeviceId, read_at AS ReadAt,
                   location_l1 AS L1, location_l2 AS L2, location_l3 AS L3
            FROM dbo.reading WHERE session_id = @sessionId AND excluded = 0
            """, new { sessionId });

        return rows.Select(r => new ReadingEntry(
            Convert.ToHexString(r.Epc),
            r.SearchKey is null ? null : Convert.ToHexString(r.SearchKey),
            r.DeviceId,
            r.ReadAt,
            r.L1 is null && r.L2 is null && r.L3 is null ? null : new ReadLocation(r.L1, r.L2, r.L3))).ToList();
    }
```

`ReadingRow` クラスを置換:

```csharp
    private sealed class ReadingRow
    {
        public byte[] Epc { get; init; } = [];
        public byte[]? SearchKey { get; init; }
        public string? DeviceId { get; init; }
        public DateTimeOffset ReadAt { get; init; }
        public string? L1 { get; init; }
        public string? L2 { get; init; }
        public string? L3 { get; init; }
    }
```

- [ ] **Step 5: テストが通ることを確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj`
Expected: PASS(新テスト + 既存統合テスト全緑)。

- [ ] **Step 6: コミット**

```bash
git add src/EpcForwarder.Infrastructure/Persistence/SqlReadingStore.cs tests/EpcForwarder.Infrastructure.Tests/SqlReadingStoreLocationTests.cs
git commit -m "fix(infra): reading の location 永続化と tenant_id を session 由来に修正"
```

---

## Task 3: IngestionDispatcher — 読取コマンドと遅延セッション生成(Core)

**Files:**
- Create: `src/EpcForwarder.Core/Ingestion/IngestionCommands.cs`
- Create: `src/EpcForwarder.Core/Ingestion/IngestionDispatcher.cs`
- Modify: `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs`
- Test: `tests/EpcForwarder.Core.Tests/Ingestion/IngestionDispatcherTests.cs`

- [ ] **Step 1: コマンド型を作成**

`src/EpcForwarder.Core/Ingestion/IngestionCommands.cs`:

```csharp
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Ingestion;

/// <summary>取込メッセージのドメイン表現(wire形式の JSON は Functions 層が本型へ変換する)。</summary>
public interface IIngestionCommand { }

/// <summary>読取1件。未知セッションは本コマンドのメタデータで遅延生成する。</summary>
public sealed record ReadCommand(
    int Tenant,
    Guid SessionId,
    string? BusinessKey,
    SessionType SessionType,
    bool ResolveSku,
    string Epc,
    string? DeviceId,
    ReadLocation? Location,
    DateTimeOffset ReadAt) : IIngestionCommand;

/// <summary>伝票の完了イベント。到達性突合のトリガー。</summary>
public sealed record CompleteCommand(int Tenant, Guid SessionId, int ExpectedCount) : IIngestionCommand;

/// <summary>完了処理の結果(ホスト側のログ用)。</summary>
public sealed record CompletionOutcome(ReachabilityResult Reachability, bool Delivered, WebhookResult? Delivery);
```

- [ ] **Step 2: `FakeDestinationCatalog` を fakes に追加**

`tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs` に追記(ファイル末尾、`using EpcForwarder.Core.Delivery;` が無ければ先頭 using に追加):

```csharp
public sealed class FakeDestinationCatalog : IDestinationCatalog
{
    private readonly Dictionary<int, List<DeliveryTarget>> _map = new();
    public void Add(int tenantId, DeliveryTarget target)
    {
        if (!_map.TryGetValue(tenantId, out var list)) { list = new(); _map[tenantId] = list; }
        list.Add(target);
    }
    public IReadOnlyList<DeliveryTarget> GetActiveTargets(int tenantId) =>
        _map.TryGetValue(tenantId, out var list) ? list : Array.Empty<DeliveryTarget>();
}
```

- [ ] **Step 3: 失敗するテストを書く(遅延生成 + 読取取込)**

`tests/EpcForwarder.Core.Tests/Ingestion/IngestionDispatcherTests.cs`:

```csharp
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Ingestion;

public class IngestionDispatcherTests
{
    private sealed class Harness
    {
        public InMemorySessionStore Sessions { get; } = new();
        public InMemoryReadingStore Readings { get; } = new();
        public InMemoryProductCatalog Products { get; } = new();
        public InMemorySnapshotStore Snapshots { get; } = new();
        public FakeSecretStore Secrets { get; } = new();
        public CapturingWebhookSender Sender { get; } = new();
        public CapturingDeviceFeedback Feedback { get; } = new();
        public FakeDestinationCatalog Destinations { get; } = new();
        public FixedClock Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public SequentialIdGenerator Ids { get; } = new();

        public IngestionDispatcher Build()
        {
            var publisher = new SnapshotPublisher(Readings, Products, Snapshots, Sender, Secrets, new PayloadBuilder(), Clock, Ids);
            var ingestor = new ReadingIngestor(Sessions, Readings, Clock);
            var reconciler = new ShipmentReconciler(Sessions, Readings, Feedback, Clock);
            var deliverer = new ShipmentDeliverer(Sessions, publisher, Clock);
            return new IngestionDispatcher(Sessions, ingestor, reconciler, deliverer, Destinations);
        }
    }

    private static ReadCommand Read(Guid id, string epc, bool resolveSku = true) =>
        new(1, id, "DN-1", SessionType.Shipment, resolveSku, epc, "devA", null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void IngestRead_UnknownSession_LazyCreatesSessionAndStoresReading()
    {
        var h = new Harness();
        var d = h.Build();
        var id = Guid.NewGuid();

        d.IngestRead(Read(id, "302DB42318A0038000001231"));

        var session = h.Sessions.Get(id);
        Assert.NotNull(session);
        Assert.Equal(1, session!.TenantId);
        Assert.Equal(SessionType.Shipment, session.Type);
        Assert.Equal("DN-1", session.BusinessKey);
        Assert.Single(h.Readings.List(id));
    }

    [Fact]
    public void IngestRead_ExistingSession_DoesNotOverwriteMetadata()
    {
        var h = new Harness();
        var d = h.Build();
        var id = Guid.NewGuid();
        h.Sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-EXISTING", h.Clock.UtcNow));

        d.IngestRead(Read(id, "302DB42318A0038000001231"));

        Assert.Equal("DN-EXISTING", h.Sessions.Get(id)!.BusinessKey);
    }
}
```

- [ ] **Step 4: 失敗を確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet build tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj`
Expected: コンパイルエラー(`IngestionDispatcher` 未定義)。

- [ ] **Step 5: `IngestionDispatcher` を実装(読取経路のみ)**

`src/EpcForwarder.Core/Ingestion/IngestionDispatcher.cs`:

```csharp
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Ingestion;

/// <summary>
/// 取込メッセージをドメイン操作へ振り分ける。読取は未知セッションを遅延生成してから取り込み、
/// 伝票の完了は到達性突合→一致時に有効な宛先へ確定配信する。
/// </summary>
public sealed class IngestionDispatcher(
    ISessionStore sessions,
    ReadingIngestor ingestor,
    ShipmentReconciler reconciler,
    ShipmentDeliverer deliverer,
    IDestinationCatalog destinations)
{
    public void IngestRead(ReadCommand cmd)
    {
        // 遅延生成: 未知セッションはメッセージのメタデータで作成(既存は触らない)。
        if (sessions.Get(cmd.SessionId) is null)
        {
            sessions.Save(new Session(cmd.SessionId, cmd.Tenant, cmd.SessionType, cmd.BusinessKey, ingestor is null ? default : default));
        }

        ingestor.Ingest(cmd.SessionId, cmd.Epc, cmd.DeviceId, cmd.ReadAt, cmd.ResolveSku, cmd.Location);
    }
}
```

> 注意: 上の `new Session(...)` の `now` 引数はプレースホルダになっている。Step 6 で `IClock` を注入して正す(このままでは Task 4 のビルドでコンパイルエラーになるため、Step 6 とセットで完成させる)。

- [ ] **Step 6: `IClock` を注入して生成時刻を正す**

`IngestionDispatcher` のコンストラクタに `IClock clock` を追加し、`IngestRead` のセッション生成を修正:

```csharp
public sealed class IngestionDispatcher(
    ISessionStore sessions,
    ReadingIngestor ingestor,
    ShipmentReconciler reconciler,
    ShipmentDeliverer deliverer,
    IDestinationCatalog destinations,
    IClock clock)
{
    public void IngestRead(ReadCommand cmd)
    {
        if (sessions.Get(cmd.SessionId) is null)
        {
            sessions.Save(new Session(cmd.SessionId, cmd.Tenant, cmd.SessionType, cmd.BusinessKey, clock.UtcNow));
        }

        ingestor.Ingest(cmd.SessionId, cmd.Epc, cmd.DeviceId, cmd.ReadAt, cmd.ResolveSku, cmd.Location);
    }
}
```

テストハーネス(Step 3)の `Build()` の `new IngestionDispatcher(...)` 末尾に `, Clock` を追加:

```csharp
            return new IngestionDispatcher(Sessions, ingestor, reconciler, deliverer, Destinations, Clock);
```

- [ ] **Step 7: テストが通ることを確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj --filter "FullyQualifiedName~IngestionDispatcherTests"`
Expected: PASS。

- [ ] **Step 8: コミット**

```bash
git add src/EpcForwarder.Core/Ingestion/ tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs tests/EpcForwarder.Core.Tests/Ingestion/IngestionDispatcherTests.cs
git commit -m "feat(core): IngestionDispatcher の読取経路(遅延セッション生成)"
```

---

## Task 4: IngestionDispatcher — 完了→到達性突合→自動配信(Core) + DI登録

**Files:**
- Modify: `src/EpcForwarder.Core/Ingestion/IngestionDispatcher.cs`
- Modify: `src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`(`IngestionDispatcher` 登録)
- Test: `tests/EpcForwarder.Core.Tests/Ingestion/IngestionDispatcherTests.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs`(解決テスト)

- [ ] **Step 1: 失敗するテストを書く(3ケース)**

`IngestionDispatcherTests.cs` に追記。冒頭の `private static ReadCommand Read(...)` の下にヘルパと宛先生成を追加し、3テストを足す:

```csharp
    private static DeliveryTarget Target(string url) =>
        new(url, "POST", "1", HmacEnabled: false, HmacSecretRef: null, Headers: new Dictionary<string, string>());

    [Fact]
    public async Task Complete_Match_DeliversToFirstActiveTarget()
    {
        var h = new Harness();
        h.Destinations.Add(1, Target("https://example.test/hook"));
        var d = h.Build();
        var id = Guid.NewGuid();

        d.IngestRead(Read(id, "302DB42318A0038000001231"));   // received = 1
        var outcome = await d.CompleteAsync(new CompleteCommand(1, id, ExpectedCount: 1));

        Assert.True(outcome.Reachability.IsMatch);
        Assert.True(outcome.Delivered);
        Assert.Equal(1, h.Sender.SendCount);
        Assert.Equal(SessionStatus.Forwarded, h.Sessions.Get(id)!.Status);
    }

    [Fact]
    public async Task Complete_Mismatch_SendsFeedback_DoesNotDeliver()
    {
        var h = new Harness();
        h.Destinations.Add(1, Target("https://example.test/hook"));
        var d = h.Build();
        var id = Guid.NewGuid();

        d.IngestRead(Read(id, "302DB42318A0038000001231"));   // received = 1
        var outcome = await d.CompleteAsync(new CompleteCommand(1, id, ExpectedCount: 2));

        Assert.False(outcome.Reachability.IsMatch);
        Assert.False(outcome.Delivered);
        Assert.Equal(0, h.Sender.SendCount);
        Assert.Single(h.Feedback.Sent);
        Assert.NotEqual(SessionStatus.Forwarded, h.Sessions.Get(id)!.Status);
    }

    [Fact]
    public async Task Complete_Match_NoActiveTarget_DoesNotDeliver()
    {
        var h = new Harness();   // 宛先未登録
        var d = h.Build();
        var id = Guid.NewGuid();

        d.IngestRead(Read(id, "302DB42318A0038000001231"));
        var outcome = await d.CompleteAsync(new CompleteCommand(1, id, ExpectedCount: 1));

        Assert.True(outcome.Reachability.IsMatch);
        Assert.False(outcome.Delivered);
        Assert.Equal(0, h.Sender.SendCount);
    }
```

- [ ] **Step 2: 失敗を確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet build tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj`
Expected: コンパイルエラー(`CompleteAsync` 未定義)。

- [ ] **Step 3: `CompleteAsync` を実装**

`IngestionDispatcher` に追加(クラス内・`IngestRead` の下):

```csharp
    /// <summary>
    /// 伝票完了。到達性を突合し、一致時のみ有効な宛先(先頭)へ確定配信する。
    /// PoC: 複数宛先のファンアウトは未対応(ShipmentDeliverer がセッション単位で1回 forwarded にするため先頭のみ)。
    /// 不一致時は ShipmentReconciler が再読取フィードバックを送る(本メソッドは配信しない)。
    /// </summary>
    public async Task<CompletionOutcome> CompleteAsync(CompleteCommand cmd, CancellationToken ct = default)
    {
        var reachability = await reconciler.CompleteAsync(cmd.SessionId, cmd.ExpectedCount, ct);
        if (!reachability.IsMatch)
        {
            return new CompletionOutcome(reachability, Delivered: false, Delivery: null);
        }

        var target = destinations.GetActiveTargets(cmd.Tenant).FirstOrDefault();
        if (target is null)
        {
            return new CompletionOutcome(reachability, Delivered: false, Delivery: null);
        }

        var delivery = await deliverer.FinalizeAndDeliverAsync(cmd.SessionId, target, ct);
        return new CompletionOutcome(reachability, Delivered: true, Delivery: delivery);
    }
```

- [ ] **Step 4: テストが通ることを確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj --filter "FullyQualifiedName~IngestionDispatcherTests"`
Expected: PASS(全5ケース)。

- [ ] **Step 5: `AddEpcForwarder` に `IngestionDispatcher` を登録**

`src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` の Core サービス登録ブロックに追記(`ProductRegistrar` 登録の直後)。ファイル先頭 using に `using EpcForwarder.Core.Ingestion;` を追加:

```csharp
        services.AddSingleton<ProductRegistrar>();
        services.AddSingleton<IngestionDispatcher>();
```

- [ ] **Step 6: DI 解決テストを追加**

`tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs` を開き、既存の解決アサーションが並ぶテスト内に1行追加(既存のスタイルに合わせる。`using EpcForwarder.Core.Ingestion;` が必要なら追加):

```csharp
        Assert.NotNull(provider.GetRequiredService<IngestionDispatcher>());
```

- [ ] **Step 7: 全テスト確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj`
Expected: PASS。

- [ ] **Step 8: コミット**

```bash
git add src/EpcForwarder.Core/Ingestion/IngestionDispatcher.cs src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/EpcForwarder.Core.Tests/Ingestion/IngestionDispatcherTests.cs tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs
git commit -m "feat(core): IngestionDispatcher 完了→突合→自動配信 と DI登録"
```

---

## Task 5: HttpWebhookSender を IHttpClientFactory 化(Infrastructure)

`AddEpcForwarder` の `services.AddSingleton(new HttpClient())`(TODO で③b化が予告済み)を `IHttpClientFactory` ベースに置き換える。シングルトン構成のため、`HttpWebhookSender` はファクトリを保持し送信毎に `CreateClient` する(ハンドラのローテーションを活かす)。

**Files:**
- Modify: `src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs`
- Modify: `src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj`
- Modify: `tests/EpcForwarder.Core.Tests/Infrastructure/HttpWebhookSenderTests.cs`
- Modify: `tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs`

- [ ] **Step 1: `Microsoft.Extensions.Http` を追加**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj package Microsoft.Extensions.Http
```
追加後、`EpcForwarder.Infrastructure.csproj` を確認し、付与された `Microsoft.Extensions.Http` のバージョンが 10.x であること(他の `Microsoft.Extensions.*` と整合)を目視確認。8.x が付いた場合は 10.x の明示指定に直す。

- [ ] **Step 2: `HttpWebhookSender` をファクトリ化**

`src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs` のクラス宣言と冒頭を置換(クライアント名定数 + ファクトリ注入。`SendAsync` 本体のメッセージ組立ロジックは現状のまま維持):

```csharp
using System.Net.Http.Headers;
using System.Text;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Delivery;

/// <summary>IWebhookSender の実HTTP実装。URLガードは上位(アプリ層)で実施済みの前提。</summary>
public sealed class HttpWebhookSender(IHttpClientFactory factory) : IWebhookSender
{
    public const string ClientName = "webhook";

    public async Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        var client = factory.CreateClient(ClientName);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        // ... 既存の Content/ヘッダ組立はそのまま ...
```

> `using var response = await client.SendAsync(message, ct);` 以降も現状維持。`client` は factory 由来のためここで `Dispose` しないこと(`using` を付けない)。

- [ ] **Step 3: `AddEpcForwarder` の HttpClient 登録を置換**

`ServiceCollectionExtensions.cs` の以下2行:

```csharp
        services.AddSingleton(new HttpClient()); // TODO: replace with IHttpClientFactory in the Functions host (③b)
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();
```

を置換:

```csharp
        services.AddHttpClient(HttpWebhookSender.ClientName);
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();
```

ファイル先頭 using に `using EpcForwarder.Infrastructure.Delivery;` が既にあること(無ければ追加)。

- [ ] **Step 4: 既存ユニットテストを factory 化**

`HttpWebhookSenderTests.cs` と `ShipmentE2ETests.cs` は `new HttpWebhookSender(http)` で `HttpClient` を直接渡している。最小のファクトリ・フェイクを介すよう修正する。`tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs` に追記:

```csharp
public sealed class SingleClientHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
```

`InMemoryFakes.cs` 先頭 using に `using System.Net.Http;` が必要なら追加。

`HttpWebhookSenderTests.cs` の `new HttpWebhookSender(http)` を全て `new HttpWebhookSender(new SingleClientHttpClientFactory(http))` に置換(`using EpcForwarder.Core.Tests.Fakes;` が無ければ追加)。

`ShipmentE2ETests.cs` の `new HttpWebhookSender(http)` を同様に `new HttpWebhookSender(new SingleClientHttpClientFactory(http))` に置換。

- [ ] **Step 5: ビルドと全テスト確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj`
Expected: PASS(`HttpWebhookSenderTests`・`ShipmentE2ETests`・`AddEpcForwarderTests` 含め全緑)。

- [ ] **Step 6: コミット**

```bash
git add src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs tests/EpcForwarder.Core.Tests/Infrastructure/HttpWebhookSenderTests.cs tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs
git commit -m "refactor(infra): HttpWebhookSender を IHttpClientFactory 化"
```

---

## Task 6: Functions ホストの DI 結線(Program.cs)

**Files:**
- Modify: `src/EpcForwarder.Functions/Program.cs`
- Modify: `src/EpcForwarder.Functions/local.settings.json`

- [ ] **Step 1: `Program.cs` に `AddEpcForwarder` を結線**

`src/EpcForwarder.Functions/Program.cs` の `builder.ConfigureFunctionsWebApplication();` の直後に追加。先頭 using に追加:

```csharp
using EpcForwarder.Infrastructure.DependencyInjection;
```

`builder.Build().Run();` の前に挿入:

```csharp
var sqlConnectionString = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("App setting 'SqlConnectionString' is required.");

builder.Services.AddEpcForwarder(new EpcForwarderOptions
{
    SqlConnectionString = sqlConnectionString,
    KeyVaultUri = builder.Configuration["KeyVaultUri"],
});
```

> `IngestionDispatcher` は `AddEpcForwarder`(Task 4)で登録済みのため、関数クラスはコンストラクタ注入で受け取れる。

- [ ] **Step 2: `local.settings.json` に設定キーを追加**

`src/EpcForwarder.Functions/local.settings.json` の `Values` に追記(ローカル開発用プレースホルダ。実値はコミットしない):

```json
{
    "IsEncrypted": false,
    "Values": {
        "AzureWebJobsStorage": "UseDevelopmentStorage=true",
        "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
        "SqlConnectionString": "Server=localhost;Database=epcf;Trusted_Connection=True;TrustServerCertificate=True",
        "KeyVaultUri": "",
        "IoTHubEventHubConnection": "",
        "IoTHubEventHubName": "messages/events"
    }
}
```

- [ ] **Step 3: ビルド確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet build src/EpcForwarder.Functions/EpcForwarder.Functions.csproj`
Expected: 警告ゼロでビルド成功。

- [ ] **Step 4: コミット**

```bash
git add src/EpcForwarder.Functions/Program.cs src/EpcForwarder.Functions/local.settings.json
git commit -m "feat(functions): Program.cs で AddEpcForwarder を構成から結線"
```

---

## Task 7: 取込 wire 契約 + パーサ(Functions) + Functions.Tests

JSON wire 形式の DTO とパーサ(`kind` 判別)を Functions 側に置き、Core のコマンドへ変換する。パーサは純粋ロジックなので新規テストプロジェクトで単体テストする(トリガーはローカル実行不可のため、せめて契約パースを緑にする)。

**Files:**
- Create: `src/EpcForwarder.Functions/Ingestion/IngestionMessages.cs`
- Create: `src/EpcForwarder.Functions/Ingestion/IngestionMessageParser.cs`
- Create: `tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj`
- Create: `tests/EpcForwarder.Functions.Tests/IngestionMessageParserTests.cs`
- Modify: `EpcForwarder.sln`

- [ ] **Step 1: wire DTO を作成**

`src/EpcForwarder.Functions/Ingestion/IngestionMessages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace EpcForwarder.Functions.Ingestion;

/// <summary>取込メッセージの判別用ヘッダ(kind だけ先読みする)。</summary>
public sealed class MessageKind
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}

/// <summary>読取イベント(wire形式)。snake_case。</summary>
public sealed class ReadMessage
{
    [JsonPropertyName("tenant")] public int Tenant { get; set; }
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("business_key")] public string? BusinessKey { get; set; }
    [JsonPropertyName("session_type")] public string SessionType { get; set; } = "";
    [JsonPropertyName("resolve_sku")] public bool ResolveSku { get; set; }
    [JsonPropertyName("epc")] public string Epc { get; set; } = "";
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("location")] public LocationDto? Location { get; set; }
    [JsonPropertyName("read_at")] public DateTimeOffset ReadAt { get; set; }
}

public sealed class LocationDto
{
    [JsonPropertyName("l1")] public string? L1 { get; set; }
    [JsonPropertyName("l2")] public string? L2 { get; set; }
    [JsonPropertyName("l3")] public string? L3 { get; set; }
}

/// <summary>完了イベント(wire形式)。</summary>
public sealed class CompleteMessage
{
    [JsonPropertyName("tenant")] public int Tenant { get; set; }
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("expected_count")] public int ExpectedCount { get; set; }
}
```

- [ ] **Step 2: パーサを作成**

`src/EpcForwarder.Functions/Ingestion/IngestionMessageParser.cs`:

```csharp
using System.Text.Json;
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Functions.Ingestion;

/// <summary>取込 JSON 1件を Core のコマンドへ変換する。未知 kind / 不正は例外。</summary>
public static class IngestionMessageParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IIngestionCommand Parse(string json)
    {
        var head = JsonSerializer.Deserialize<MessageKind>(json, Options)
            ?? throw new FormatException("Empty ingestion message.");

        return head.Kind switch
        {
            "read" => ToRead(JsonSerializer.Deserialize<ReadMessage>(json, Options)!),
            "complete" => ToComplete(JsonSerializer.Deserialize<CompleteMessage>(json, Options)!),
            _ => throw new FormatException($"Unknown ingestion message kind: '{head.Kind}'."),
        };
    }

    private static ReadCommand ToRead(ReadMessage m) => new(
        m.Tenant,
        m.SessionId,
        m.BusinessKey,
        Enum.Parse<SessionType>(m.SessionType, ignoreCase: true),
        m.ResolveSku,
        m.Epc,
        m.DeviceId,
        m.Location is null ? null : new ReadLocation(m.Location.L1, m.Location.L2, m.Location.L3),
        m.ReadAt);

    private static CompleteCommand ToComplete(CompleteMessage m) => new(m.Tenant, m.SessionId, m.ExpectedCount);
}
```

- [ ] **Step 3: Functions.Tests プロジェクトを作成**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
cd /home/hiro/repos/epc-forwarder
dotnet new xunit -o tests/EpcForwarder.Functions.Tests -n EpcForwarder.Functions.Tests
dotnet add tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj reference src/EpcForwarder.Functions/EpcForwarder.Functions.csproj
dotnet sln EpcForwarder.sln add tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj
```

生成された `tests/EpcForwarder.Functions.Tests/UnitTest1.cs` があれば削除:
```bash
rm -f tests/EpcForwarder.Functions.Tests/UnitTest1.cs
```

> `EpcForwarder.Functions` は `OutputType=Exe` だがプロジェクト参照によるテストは可能。ビルドで問題が出る場合のみ、参照ではなく対象2ファイル(`IngestionMessages.cs`/`IngestionMessageParser.cs`)を `<Compile Include="..." Link="..."/>` で取り込む方式に切替える(まずは参照方式で試すこと)。

- [ ] **Step 4: パーサの失敗テストを書く**

`tests/EpcForwarder.Functions.Tests/IngestionMessageParserTests.cs`:

```csharp
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Functions.Ingestion;
using Xunit;

namespace EpcForwarder.Functions.Tests;

public class IngestionMessageParserTests
{
    [Fact]
    public void Parse_Read_MapsAllFields()
    {
        const string json = """
        {"kind":"read","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001",
         "business_key":"DN-1","session_type":"shipment","resolve_sku":true,
         "epc":"302DB42318A0038000001231","device_id":"handy-07",
         "location":{"l1":"TOKYO-DC","l2":"2F","l3":"A-01"},"read_at":"2026-06-18T12:30:01Z"}
        """;

        var cmd = Assert.IsType<ReadCommand>(IngestionMessageParser.Parse(json));
        Assert.Equal(1, cmd.Tenant);
        Assert.Equal(Guid.Parse("9c3a8f10-0000-0000-0000-000000000001"), cmd.SessionId);
        Assert.Equal(SessionType.Shipment, cmd.SessionType);
        Assert.True(cmd.ResolveSku);
        Assert.Equal("302DB42318A0038000001231", cmd.Epc);
        Assert.Equal("handy-07", cmd.DeviceId);
        Assert.Equal("TOKYO-DC", cmd.Location!.L1);
        Assert.Equal("A-01", cmd.Location!.L3);
    }

    [Fact]
    public void Parse_Complete_MapsFields()
    {
        const string json = """
        {"kind":"complete","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001","expected_count":45}
        """;

        var cmd = Assert.IsType<CompleteCommand>(IngestionMessageParser.Parse(json));
        Assert.Equal(45, cmd.ExpectedCount);
    }

    [Fact]
    public void Parse_UnknownKind_Throws()
    {
        Assert.Throws<FormatException>(() => IngestionMessageParser.Parse("""{"kind":"bogus"}"""));
    }
}
```

- [ ] **Step 5: テスト確認(red→green)**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet test tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj`
Expected: PASS(DTO/パーサ実装済みのため緑。コンパイル不能なら参照方式を Step 3 注記の方式へ切替)。

- [ ] **Step 6: コミット**

```bash
git add src/EpcForwarder.Functions/Ingestion/IngestionMessages.cs src/EpcForwarder.Functions/Ingestion/IngestionMessageParser.cs tests/EpcForwarder.Functions.Tests/ EpcForwarder.sln
git commit -m "feat(functions): 取込 wire 契約と JSON パーサ + Functions.Tests"
```

---

## Task 8: EventHub トリガー Ingestion 関数(Functions・ビルド検証)

**Files:**
- Create: `src/EpcForwarder.Functions/Ingestion/IngestionFunction.cs`
- Modify: `src/EpcForwarder.Functions/EpcForwarder.Functions.csproj`
- Delete: `src/EpcForwarder.Functions/Ingestion/.gitkeep`

- [ ] **Step 1: EventHubs Worker 拡張を追加**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add src/EpcForwarder.Functions/EpcForwarder.Functions.csproj package Microsoft.Azure.Functions.Worker.Extensions.EventHubs
```

- [ ] **Step 2: Ingestion 関数を実装**

`src/EpcForwarder.Functions/Ingestion/IngestionFunction.cs`:

```csharp
using EpcForwarder.Core.Ingestion;
using EpcForwarder.Functions.Ingestion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace EpcForwarder.Functions.Ingestion;

/// <summary>
/// IoT Hub 内蔵 Event Hubs 互換エンドポイントからの取込。読取/完了をバッチで受け Dispatcher へ流す。
/// バインディング自体はローカル実行不可のためビルド検証のみ。
/// </summary>
public sealed class IngestionFunction(IngestionDispatcher dispatcher, ILogger<IngestionFunction> logger)
{
    [Function("Ingestion")]
    public async Task Run(
        [EventHubTrigger("%IoTHubEventHubName%", Connection = "IoTHubEventHubConnection", IsBatched = true)] string[] messages,
        CancellationToken ct)
    {
        foreach (var raw in messages)
        {
            IIngestionCommand command;
            try
            {
                command = IngestionMessageParser.Parse(raw);
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                // PoC: 不正メッセージはログのみ(at-least-once 再処理での毒メッセージ防止)。
                logger.LogWarning(ex, "Skipping malformed ingestion message.");
                continue;
            }

            switch (command)
            {
                case ReadCommand read:
                    dispatcher.IngestRead(read);
                    break;
                case CompleteCommand complete:
                    var outcome = await dispatcher.CompleteAsync(complete, ct);
                    logger.LogInformation(
                        "Completion session={SessionId} expected={Expected} received={Received} delivered={Delivered}",
                        complete.SessionId, outcome.Reachability.Expected, outcome.Reachability.Received, outcome.Delivered);
                    break;
            }
        }
    }
}
```

- [ ] **Step 3: プレースホルダ削除**

```bash
git rm src/EpcForwarder.Functions/Ingestion/.gitkeep
```

- [ ] **Step 4: ビルド確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet build src/EpcForwarder.Functions/EpcForwarder.Functions.csproj`
Expected: 警告ゼロでビルド成功(Functions メタデータ生成も成功)。

- [ ] **Step 5: コミット**

```bash
git add src/EpcForwarder.Functions/Ingestion/IngestionFunction.cs src/EpcForwarder.Functions/EpcForwarder.Functions.csproj
git commit -m "feat(functions): IoT Hub(EventHub)トリガー Ingestion 関数"
```

---

## Task 9: 取込契約ドキュメント + 全体検証

**Files:**
- Create: `docs/design/ingestion-contract.md`

- [ ] **Step 1: 取込契約ドキュメントを作成**

`docs/design/ingestion-contract.md` に、本実装で確定した wire 契約を記述:

````markdown
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

### complete(伝票の完了イベント)
```json
{ "kind": "complete", "tenant": 1, "session_id": "9c3a8f10-...", "expected_count": 45 }
```
- 到達性突合(received = ユニーク読取数 vs expected_count)。
- **一致**: 有効な宛先(先頭)へ確定スナップショットを配信し forwarded。
- **不一致**: 端末へ再読取フィードバック(現状 NullDeviceFeedback の no-op)、配信しない。

## PoC の制約・既知ギャップ
- **settle 猶予なし**: complete が read を追い越すと過少 received で不一致になりうる。再突合/猶予は将来対応(基本設計2.1 のとおり数百ms〜数秒の猶予 or 再突合)。
- **複数宛先のファンアウト未対応**: `IDestinationCatalog.GetActiveTargets` の先頭のみへ配信(ShipmentDeliverer がセッション単位で1回 forwarded にするため)。
- **棚卸の完了/仮確定は本経路ではない**: HTTP(③b-2)で起動。complete は伝票用。
- **毒メッセージ**: パース不能な read/complete はログのみでスキップ(at-least-once 再処理での停滞防止)。
- **C2D 宛先デバイスID**: `IDeviceFeedback` がまだ宛先デバイスを運んでいない(③b 以降)。
````

- [ ] **Step 2: 全プロジェクトのビルドと全テストを実行**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build EpcForwarder.sln
dotnet test EpcForwarder.sln
```
Expected: 警告ゼロでビルド成功。全テスト PASS(Docker 稼働下で統合テスト含む)。

- [ ] **Step 3: コミット**

```bash
git add docs/design/ingestion-contract.md
git commit -m "docs: 取込(Ingestion)契約の詳細設計"
```

---

## Self-Review チェック結果

- **スコープ網羅(③b-1 = 書き込み経路)**: 取込トリガー(Task 8)・遅延セッション生成(Task 3)・完了→突合→自動配信(Task 4)・宛先解決(Task 4, `IDestinationCatalog`)・DI 結線(Task 6)・`IHttpClientFactory` 化(Task 5、既存 TODO 解消)・location 配管(Task 1,2)・tenant_id バグ修正(Task 2)・wire 契約(Task 7,9)を網羅。クエリAPI/棚卸HTTP/認可は ③b-2 へ分離(意図的)。
- **型整合**: `ReadLocation`(L1/L2/L3)・`ReadCommand`/`CompleteCommand`/`CompletionOutcome`・`IngestionDispatcher(sessions, ingestor, reconciler, deliverer, destinations, clock)`・`HttpWebhookSender.ClientName="webhook"` を全タスクで一貫使用。`ReadingIngestor.Ingest(..., resolveSku, location=null)` と `ReadingEntry(..., Location=null)` の任意引数で既存呼び出し互換。
- **プレースホルダ**: Task 3 Step 5 に意図的な未完コードがあるが、直後の Step 6 で `IClock` 注入により確定させる旨を明記済み(他に TBD/TODO 残置なし)。
- **検証**: 各 Core/Infra タスクは TDD(red→green)。Functions トリガーはローカル実行不可のためビルド検証 + パーサ単体(Task 7)で担保。
