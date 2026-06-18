# 棚卸スライス（仮確定＋確定）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 棚卸（inventory）の配信スライスをインプロセスで実装する。締め切り前に現時点の集約を何度でも送れる **仮確定スナップショット**（`is_final=false`、セッションは `open` のまま）と、締め切りの **確定スナップショット**（`is_final=true`、`forwarded`）を提供する。件数突合は行わない（基本設計2.2）。

**Architecture:** 伝票と棚卸で重複する「スナップショット1件の組み立て→署名→送信→記録」を `SnapshotPublisher` に抽出し、`ShipmentDeliverer` をそれを使う形にリファクタする（伝票テストが回帰ガード）。`InventoryDeliverer` は `SnapshotPublisher` を用い、セッション状態遷移（仮確定はopen維持／確定はfinalize→forwarded）だけを担う。取込は既存 `ReadingIngestor`（EPC単位の後勝ち）をそのまま再利用する。

**Tech Stack:** C# / .NET 8、xUnit。既存 `SkuAggregator`/`PayloadBuilder`/`HmacSigner`/ポート/フェイクを再利用。

**Scope boundary:**
- 含む: `SnapshotPublisher` 抽出＋`ShipmentDeliverer` リファクタ、`InventoryDeliverer`（仮確定＋確定）、棚卸インプロセス・フローテスト。
- 含まない（別スライス）: ロケーション付き取込・ロケ別の端末フィードバック（pull API）、仮確定の定期スケジューラ、`allow_provisional` のゲーティング（PoCは呼び出し側判断）、Azureアダプタ。
- 注: 棚卸の連携ペイロードは**セッション単位のSKU集約**（ロケ別ではない）。ロケ別明細は端末フィードバック側スライスの担当。

**Prerequisites:** `dotnet` は ~/.dotnet。各コマンド前に `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH`。全テスト: `dotnet test EpcForwarder.sln --nologo`。`Directory.Build.props` は `TreatWarningsAsErrors=true`。

---

## File Structure

| ファイル | 責務 |
|---|---|
| `src/EpcForwarder.Core/Delivery/SnapshotPublisher.cs`（新規） | スナップショット1件の集約→署名→送信→記録（状態遷移は持たない） |
| `src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs`（変更） | `SnapshotPublisher` 利用へリファクタ。`DeliveryTarget` 定義は据置 |
| `src/EpcForwarder.Core/Delivery/InventoryDeliverer.cs`（新規） | 仮確定（open維持）＋確定（forwarded） |
| `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs`（変更） | `CapturingWebhookSender` に全リクエスト記録 `Requests` を追加 |
| `tests/EpcForwarder.Core.Tests/Delivery/ShipmentDelivererTests.cs`（変更） | 新コンストラクタ（publisher注入）に合わせて構築を更新（アサーションは不変） |
| `tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs`（変更） | 同上（publisher注入） |
| `tests/EpcForwarder.Core.Tests/Delivery/SnapshotPublisherTests.cs`（新規） | `is_final=false` の挙動（新規パラメータ）を直接検証 |
| `tests/EpcForwarder.Core.Tests/Delivery/InventoryDelivererTests.cs`（新規） | 仮確定/確定の状態遷移とスナップショット |
| `tests/EpcForwarder.Core.Tests/InventoryFlowTests.cs`（新規） | 取込(後勝ち)→仮確定→追加取込→確定 のフロー |

---

## Task 1: SnapshotPublisher 抽出 ＋ ShipmentDeliverer リファクタ

既存の伝票配信から「集約→署名→送信→記録」を `SnapshotPublisher.PublishAsync(session, type, isFinal, target)` に抜き出す。`ShipmentDeliverer` は状態遷移（forwardedガード／open時finalize＋save／成功時markforwarded）だけ残す。伝票テスト（HMAC厳密・送信失敗・forwardedガード・未知タグ等）は構築だけ更新して**全て緑のまま**であること。

**Files:**
- Create: `src/EpcForwarder.Core/Delivery/SnapshotPublisher.cs`
- Modify: `src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs`
- Modify: `tests/EpcForwarder.Core.Tests/Delivery/ShipmentDelivererTests.cs`, `tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs`
- Create: `tests/EpcForwarder.Core.Tests/Delivery/SnapshotPublisherTests.cs`

- [ ] **Step 1: Create `SnapshotPublisher`**

```csharp
// src/EpcForwarder.Core/Delivery/SnapshotPublisher.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Delivery;

/// <summary>
/// スナップショット1件(SKU集約＋未知タグ分離)を組み立て、ヘッダ/HMACを付けて送信し、記録する共通処理。
/// セッションの状態遷移(finalize/forwarded)は呼び出し側(各Deliverer)が担う。
/// </summary>
public sealed class SnapshotPublisher(
    IReadingStore readings,
    IProductCatalog products,
    ISnapshotStore snapshots,
    IWebhookSender sender,
    ISecretStore secrets,
    PayloadBuilder payloadBuilder,
    IClock clock,
    IIdGenerator ids)
{
    public async Task<WebhookResult> PublishAsync(Session session, string type, bool isFinal, DeliveryTarget target, CancellationToken ct = default)
    {
        var resolved = new List<string>();
        var unknown = new List<string>();
        // PoC assumption: raw-mode EPCs (SearchKey null) land in unknown_tags together with genuine catalog misses.
        foreach (var entry in readings.List(session.PublicId))
        {
            var sku = entry.SearchKey is null ? null : products.ResolveSku(session.TenantId, entry.SearchKey);
            if (sku is null)
            {
                unknown.Add(entry.Epc);
            }
            else
            {
                resolved.Add(sku);
            }
        }

        var items = SkuAggregator.Aggregate(resolved);
        // PoC assumption: the snapshot version is consumed before send, so a throw mid-send leaves a version gap.
        var version = snapshots.NextVersion(session.PublicId);
        var idempotencyKey = ids.NewGuid();
        var generatedAt = clock.UtcNow;

        var envelope = new WebhookEnvelope(
            SchemaVersion: target.SchemaVersion,
            Tenant: session.TenantId.ToString(),
            SessionId: session.PublicId,
            BusinessKey: session.BusinessKey,
            Type: type,
            SnapshotVersion: version,
            IsFinal: isFinal,
            IdempotencyKey: idempotencyKey,
            GeneratedAt: generatedAt,
            Items: items,
            UnknownTags: new UnknownTags(unknown));

        var body = payloadBuilder.Serialize(envelope);
        var timestamp = generatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Idempotency-Key"] = idempotencyKey.ToString(),
            ["X-EPCF-Tenant"] = session.TenantId.ToString(),
            ["X-EPCF-Session"] = session.PublicId.ToString(),
            ["X-EPCF-Snapshot-Version"] = version.ToString(),
            ["X-EPCF-Schema-Version"] = target.SchemaVersion,
            ["X-EPCF-Is-Final"] = isFinal ? "true" : "false",
            ["X-EPCF-Timestamp"] = timestamp,
        };

        // Fail closed: a configured secret that cannot be resolved must abort the send.
        foreach (var (name, secretRef) in target.Headers)
        {
            var value = await secrets.GetAsync(secretRef, ct)
                ?? throw new InvalidOperationException($"Secret '{secretRef}' for header '{name}' not found.");
            headers[name] = value;
        }

        if (target.HmacEnabled)
        {
            if (target.HmacSecretRef is null)
            {
                throw new InvalidOperationException("HMAC is enabled but no HmacSecretRef is configured.");
            }

            var key = await secrets.GetAsync(target.HmacSecretRef, ct)
                ?? throw new InvalidOperationException($"HMAC secret '{target.HmacSecretRef}' not found.");
            headers["X-EPCF-Signature"] = HmacSigner.Sign(key, timestamp, body);
        }

        var result = await sender.SendAsync(new WebhookRequest(target.Url, target.Method, headers, body), ct);
        snapshots.Record(new SnapshotRecord(session.PublicId, version, isFinal, idempotencyKey, items.Count, result.Success));
        return result;
    }
}
```

- [ ] **Step 2: Refactor `ShipmentDeliverer` to use the publisher**

Replace the entire body of `src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs` with:

```csharp
// src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Delivery;

/// <summary>宛先設定の実行時投影。機密値は SecretRef を ISecretStore で解決する。</summary>
public sealed record DeliveryTarget(
    string Url,
    string Method,
    string SchemaVersion,
    bool HmacEnabled,
    string? HmacSecretRef,
    IReadOnlyDictionary<string, string> Headers); // headerName -> secretRef

/// <summary>伝票の確定→集約→送信→forwarded。配信本体は SnapshotPublisher に委譲。</summary>
public sealed class ShipmentDeliverer(ISessionStore sessions, SnapshotPublisher publisher, IClock clock)
{
    public async Task<WebhookResult> FinalizeAndDeliverAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        if (session.Status == SessionStatus.Forwarded)
        {
            throw new InvalidOperationException($"Session {sessionId} is already forwarded; use the re-send flow.");
        }

        // Finalize is idempotent + persisted so a failed send can be retried without throwing.
        if (session.Status == SessionStatus.Open)
        {
            session.Finalize(clock.UtcNow);
            sessions.Save(session);
        }

        var result = await publisher.PublishAsync(session, "shipment", isFinal: true, target, ct);

        if (result.Success)
        {
            session.MarkForwarded(clock.UtcNow);
            sessions.Save(session);
        }

        return result;
    }
}
```

- [ ] **Step 3: Update the existing shipment test construction to inject the publisher**

In `ShipmentDelivererTests.cs` and `ShipmentE2ETests.cs`, wherever a `ShipmentDeliverer` is constructed, build a `SnapshotPublisher` first and pass it. The existing 9-argument call becomes:

```csharp
var publisher = new SnapshotPublisher(readings, products, snapshots, sender, secrets, new PayloadBuilder(), clock, ids);
var sut = new ShipmentDeliverer(sessions, publisher, clock);
```
(Use the local variable names already present in each test. In `ShipmentE2ETests.cs` the deliverer uses the real `HttpWebhookSender` — pass that as the publisher's `sender`.) **Do not change any assertions.** All shipment behavior (payload, headers, HMAC equality, snapshot, status, send-failure, forwarded-guard, unknown-tags) must remain asserted and green.

- [ ] **Step 4: Add a focused publisher test for the new `isFinal` parameter**

```csharp
// tests/EpcForwarder.Core.Tests/Delivery/SnapshotPublisherTests.cs
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class SnapshotPublisherTests
{
    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task Publish_SetsIsFinal_OnEnvelopeHeaderAndSnapshot(bool isFinal, string headerValue)
    {
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snapshots = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        var publisher = new SnapshotPublisher(readings, products, snapshots, sender, secrets, new PayloadBuilder(), clock, ids);

        var session = new Session(Guid.NewGuid(), 1, SessionType.Inventory, "CAMP-1", clock.UtcNow);

        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null,
            new Dictionary<string, string>());

        var result = await publisher.PublishAsync(session, "inventory", isFinal, target);

        Assert.True(result.Success);
        Assert.Equal(headerValue, sender.Last!.Headers["X-EPCF-Is-Final"]);
        Assert.Contains($"\"is_final\":{headerValue}", sender.Last!.Body);
        var snap = Assert.Single(snapshots.Records);
        Assert.Equal(isFinal, snap.IsFinal);
        Assert.Equal(1, snap.Version);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: 全PASS（既存の伝票テスト66件＋新規publisherテストが緑。コンパイルエラー＝コンストラクタ更新漏れなので直す）。

- [ ] **Step 6: Commit**

```bash
git add src/EpcForwarder.Core/Delivery/SnapshotPublisher.cs src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs tests/EpcForwarder.Core.Tests/Delivery/ShipmentDelivererTests.cs tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs tests/EpcForwarder.Core.Tests/Delivery/SnapshotPublisherTests.cs
git commit -m "refactor(core): SnapshotPublisher を抽出し ShipmentDeliverer を再配線"
```

---

## Task 2: InventoryDeliverer（仮確定＋確定）

**Files:**
- Create: `src/EpcForwarder.Core/Delivery/InventoryDeliverer.cs`
- Modify: `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs`（`CapturingWebhookSender` に `Requests` 追加）
- Test: `tests/EpcForwarder.Core.Tests/Delivery/InventoryDelivererTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Delivery/InventoryDelivererTests.cs
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class InventoryDelivererTests
{
    private static (InventoryDeliverer Sut, Guid Id, InMemorySessionStore Sessions, InMemorySnapshotStore Snaps, CapturingWebhookSender Sender)
        Build()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snaps = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        var publisher = new SnapshotPublisher(readings, products, snaps, sender, secrets, new PayloadBuilder(), clock, ids);

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "CAMP-1", clock.UtcNow));
        return (new InventoryDeliverer(sessions, publisher, clock), id, sessions, snaps, sender);
    }

    private static DeliveryTarget Target() =>
        new("https://api.example.com/hook", "POST", "1", false, null, new Dictionary<string, string>());

    [Fact]
    public async Task Provisional_KeepsSessionOpen_AndMarksNotFinal()
    {
        var (sut, id, sessions, snaps, sender) = Build();

        var result = await sut.SendProvisionalAsync(id, Target());

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Open, sessions.Get(id)!.Status); // open のまま
        Assert.Equal("false", sender.Last!.Headers["X-EPCF-Is-Final"]);
        Assert.False(snaps.Records.Single().IsFinal);
    }

    [Fact]
    public async Task Provisional_CanRepeat_VersionsIncrement_SessionStaysOpen()
    {
        var (sut, id, sessions, snaps, _) = Build();

        await sut.SendProvisionalAsync(id, Target());
        await sut.SendProvisionalAsync(id, Target());

        Assert.Equal(SessionStatus.Open, sessions.Get(id)!.Status);
        Assert.Equal(new[] { 1, 2 }, snaps.Records.Select(r => r.Version).ToArray());
        Assert.All(snaps.Records, r => Assert.False(r.IsFinal));
    }

    [Fact]
    public async Task Finalize_SendsFinal_AndForwards()
    {
        var (sut, id, sessions, snaps, sender) = Build();

        var result = await sut.FinalizeAndDeliverAsync(id, Target());

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.Equal("true", sender.Last!.Headers["X-EPCF-Is-Final"]);
        Assert.True(snaps.Records.Single().IsFinal);
    }

    [Fact]
    public async Task Provisional_AfterForwarded_Throws()
    {
        var (sut, id, _, _, _) = Build();
        await sut.FinalizeAndDeliverAsync(id, Target());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SendProvisionalAsync(id, Target()));
    }

    [Fact]
    public async Task ProvisionalThenFinalize_ProducesTwoSnapshots_FinalLast()
    {
        var (sut, id, sessions, snaps, sender) = Build();

        await sut.SendProvisionalAsync(id, Target());
        var result = await sut.FinalizeAndDeliverAsync(id, Target());

        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.Equal(2, sender.Requests.Count);
        Assert.Equal(new[] { false, true }, snaps.Records.Select(r => r.IsFinal).ToArray());
        Assert.Equal(new[] { 1, 2 }, snaps.Records.Select(r => r.Version).ToArray());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~InventoryDelivererTests`
Expected: コンパイル失敗（`InventoryDeliverer` 未定義、`sender.Requests` 未定義）。

- [ ] **Step 3: Extend `CapturingWebhookSender` and implement `InventoryDeliverer`**

In `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs`, replace the `CapturingWebhookSender` class with:

```csharp
public sealed class CapturingWebhookSender : IWebhookSender
{
    public WebhookRequest? Last { get; private set; }
    public int SendCount { get; private set; }
    public List<WebhookRequest> Requests { get; } = new();
    public WebhookResult Next { get; set; } = new(true, 200);

    public Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        Last = request;
        Requests.Add(request);
        SendCount++;
        return Task.FromResult(Next);
    }
}
```

```csharp
// src/EpcForwarder.Core/Delivery/InventoryDeliverer.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Delivery;

/// <summary>
/// 棚卸の配信。締め切り前の仮確定(セッションはopen維持・繰り返し可)と、締め切りの確定(forwarded)を提供する。
/// 件数突合は行わない(基本設計2.2)。配信本体は SnapshotPublisher に委譲。
/// </summary>
public sealed class InventoryDeliverer(ISessionStore sessions, SnapshotPublisher publisher, IClock clock)
{
    /// <summary>締め切り前に現時点の集約スナップショットを送る。セッションは open のまま。</summary>
    public async Task<WebhookResult> SendProvisionalAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        if (session.Status != SessionStatus.Open)
        {
            throw new InvalidOperationException($"Provisional send requires an open session (was {session.Status}).");
        }

        var result = await publisher.PublishAsync(session, "inventory", isFinal: false, target, ct);
        session.Touch(clock.UtcNow);
        sessions.Save(session);
        return result;
    }

    /// <summary>締め切り。確定スナップショットを送り forwarded にする。</summary>
    public async Task<WebhookResult> FinalizeAndDeliverAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        if (session.Status == SessionStatus.Forwarded)
        {
            throw new InvalidOperationException($"Session {sessionId} is already forwarded; use the re-send flow.");
        }

        if (session.Status == SessionStatus.Open)
        {
            session.Finalize(clock.UtcNow);
            sessions.Save(session);
        }

        var result = await publisher.PublishAsync(session, "inventory", isFinal: true, target, ct);

        if (result.Success)
        {
            session.MarkForwarded(clock.UtcNow);
            sessions.Save(session);
        }

        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~InventoryDelivererTests`
Expected: PASS（5ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Delivery/InventoryDeliverer.cs tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs tests/EpcForwarder.Core.Tests/Delivery/InventoryDelivererTests.cs
git commit -m "feat(core): InventoryDeliverer(仮確定＋確定)"
```

---

## Task 3: 棚卸インプロセス・フローテスト

取込（後勝ち含む）→ 仮確定送信（open・v1・is_final=false）→ 追加取込 → 確定送信（forwarded・v2・is_final=true）を1本で検証する。

**Files:**
- Test: `tests/EpcForwarder.Core.Tests/InventoryFlowTests.cs`

- [ ] **Step 1: Write the flow test**

```csharp
// tests/EpcForwarder.Core.Tests/InventoryFlowTests.cs
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests;

public class InventoryFlowTests
{
    [Fact]
    public async Task Inventory_Ingest_Provisional_MoreIngest_Finalize()
    {
        // --- 組み立て ---
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snaps = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var publisher = new SnapshotPublisher(readings, products, snaps, sender, secrets, new PayloadBuilder(), clock, ids);
        var inventory = new InventoryDeliverer(sessions, publisher, clock);

        // 同一商品(同一検索キー)・別シリアルの2タグ
        const string epcA = "302DB42318A0038000001231";
        const string epcB = "302DB42318A0038000009999";
        var key = Sgtin96.DeriveSearchKey(epcA);
        products.Add(1, key, "ITEM-AAA");

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Inventory, "CAMP-1", clock.UtcNow));
        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null,
            new Dictionary<string, string>());

        // --- 取込(epcAを2回読む=後勝ちで1件に収束) + epcB ---
        ingestor.Ingest(id, epcA, "devA", clock.UtcNow, resolveSku: true);
        ingestor.Ingest(id, epcA, "devB", clock.UtcNow, resolveSku: true); // 重複読取 → 上書き
        ingestor.Ingest(id, epcB, "devA", clock.UtcNow, resolveSku: true);

        // --- 仮確定 ---
        await inventory.SendProvisionalAsync(id, target);
        Assert.Equal(SessionStatus.Open, sessions.Get(id)!.Status);
        Assert.Contains("\"is_final\":false", sender.Requests[0].Body);
        Assert.Contains("\"quantity\":2", sender.Requests[0].Body); // epcA(1) + epcB(1)

        // --- 追加取込(同一商品の3タグ目) ---
        ingestor.Ingest(id, "302DB42318A0038000007777", "devA", clock.UtcNow, resolveSku: true);

        // --- 締め切り(確定) ---
        var final = await inventory.FinalizeAndDeliverAsync(id, target);

        // --- 検証 ---
        Assert.True(final.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.Equal(2, sender.Requests.Count);
        Assert.Contains("\"is_final\":true", sender.Requests[1].Body);
        Assert.Contains("\"quantity\":3", sender.Requests[1].Body); // 3タグに増加
        Assert.Equal(new[] { 1, 2 }, snaps.Records.Select(r => r.Version).ToArray());
        Assert.Equal(new[] { false, true }, snaps.Records.Select(r => r.IsFinal).ToArray());
    }
}
```

- [ ] **Step 2: Run the flow test**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~InventoryFlowTests`
Expected: PASS。

- [ ] **Step 3: Run the full suite**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: 全PASS。

- [ ] **Step 4: Commit**

```bash
git add tests/EpcForwarder.Core.Tests/InventoryFlowTests.cs
git commit -m "test: 棚卸インプロセス・フロー(取込→仮確定→追加→確定)"
```

---

## 完了条件

- `dotnet build` 0警告/0エラー、`dotnet test` 全緑（伝票テストも回帰なし）。
- 棚卸セッションで、締め切り前に仮確定スナップショットを繰り返し送れ（open維持・version単調増加・is_final=false）、締め切りで確定スナップショット（is_final=true・forwarded）を送れる。後勝ち取込が集約数量に反映される。
- 後続スライス: ロケーション付き取込（`reading.location_l1/l2/l3`）とロケ別の端末フィードバック（pull API、`device-feedback.md`）、仮確定の定期スケジューラ。Azureアダプタ計画で各ポートを実装。
