# 伝票E2E（インプロセス）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 伝票（入出荷）の処理パイプライン「読取取込 → EPC&MaskでSKU化 → 後勝ち蓄積 → 完了＋到達性突合 → SKU集約 → 実HTTP Webhook送信」を、インプロセスで動作・テスト可能な形で実装する。

**Architecture:** ドメインロジックは `EpcForwarder.Core` に隔離し、外部依存（永続化・シークレット・送信・端末通知・時刻・ID）はポート（インターフェース）で抽象化する。テストは in-memory フェイクで純粋ロジックを検証し、最後に実HTTP送信（`HttpListener`）でE2Eを通す。Azure固有アダプタ（IoT Hubトリガー / Azure SQL / Key Vault / C2D）は本計画のスコープ外（第2計画）。

**Tech Stack:** C# / .NET 8（isolated worker）、xUnit、System.Text.Json（`JsonNamingPolicy.SnakeCaseLower`）、System.Security.Cryptography（HMACSHA256）、`HttpClient` / `HttpListener`。

**Scope boundary（重要）:**
- 含む: `Core` のドメイン＋アプリケーションサービス、`Infrastructure` の `HttpWebhookSender`、in-memory フェイク、インプロセスE2Eテスト。
- 含まない（第2計画）: IoT Hubトリガー関数、Azure SQL リポジトリ実装、Key Vault 実装、C2D 実装、Bicep。これらは本計画のポートを実装する形で後続。

**Prerequisites:** 各 `dotnet` コマンドは以下を前提（未永続化なら都度実行）:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
```
全テスト実行: `dotnet test EpcForwarder.sln --nologo`

**注意（warnings-as-errors）:** ルートの `Directory.Build.props` は `TreatWarningsAsErrors=true`。テストプロジェクトに付く xUnit アナライザ警告がエラー化してビルドを止める場合がある。発生したら指摘どおり修正するのが基本だが、テスト由来で本質的でない場合は `tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj` に `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` を設定して回避してよい（src側は維持）。

**ポート占有:** Task 12/13 は `HttpListener` で非特権ポート（18790/18791）を使う。CI/ローカルで競合する場合は空きポートに変更する。

---

## File Structure

新規作成/変更するファイルと責務:

| ファイル | 責務 |
|---|---|
| `src/EpcForwarder.Core/Epc/Sgtin96.cs` | SGTIN-96 のhex検証と検索キー導出（既存 `EpcKey` を利用） |
| `src/EpcForwarder.Core/Sessions/SessionStatus.cs` | セッション状態 enum |
| `src/EpcForwarder.Core/Sessions/SessionType.cs` | セッション種別 enum |
| `src/EpcForwarder.Core/Sessions/Session.cs` | セッション集約（状態遷移を強制） |
| `src/EpcForwarder.Core/Abstractions/Ports.cs` | 外部依存ポート（時刻/ID/各ストア/送信/通知） |
| `src/EpcForwarder.Core/Delivery/WebhookModels.cs` | 送信ペイロードのモデル（envelope/items） |
| `src/EpcForwarder.Core/Delivery/SkuAggregator.cs` | SKU別数量集約（純関数） |
| `src/EpcForwarder.Core/Delivery/HmacSigner.cs` | HMAC-SHA256署名 |
| `src/EpcForwarder.Core/Delivery/WebhookUrlGuard.cs` | 送信先URL検証（SSRFガード） |
| `src/EpcForwarder.Core/Delivery/PayloadBuilder.cs` | envelope→JSON（snake_case） |
| `src/EpcForwarder.Core/Sessions/ReadingIngestor.cs` | 読取取込（後勝ちUPSERT） |
| `src/EpcForwarder.Core/Sessions/ShipmentReconciler.cs` | 完了＋到達性突合＋不一致通知 |
| `src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs` | 確定→集約→署名→送信→スナップショット記録→forwarded |
| `src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs` | 実HTTP送信（`IWebhookSender`実装） |
| `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs` | テスト用 in-memory フェイク群 |
| `tests/EpcForwarder.Core.Tests/**/...Tests.cs` | 各単体テスト |
| `tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs` | インプロセスE2E（実HTTP受信） |

---

## Task 1: SGTIN-96 検索キー導出（hex入口）

**Files:**
- Create: `src/EpcForwarder.Core/Epc/Sgtin96.cs`
- Test: `tests/EpcForwarder.Core.Tests/Epc/Sgtin96Tests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Epc/Sgtin96Tests.cs
using EpcForwarder.Core.Epc;
using Xunit;

namespace EpcForwarder.Core.Tests.Epc;

public class Sgtin96Tests
{
    [Theory]
    [InlineData("302DB42318A0038000001231", "302DB42318A0038000000000")]
    [InlineData("302db42318a0038000009999", "302DB42318A0038000000000")] // 小文字入力も許容
    public void DeriveSearchKey_ZeroesSerial(string epcHex, string expected)
    {
        Assert.Equal(expected, Sgtin96.DeriveSearchKey(epcHex));
    }

    [Theory]
    [InlineData("302DB42318A00380000012")]   // 22桁（短い）
    [InlineData("302DB42318A0038000001231AA")] // 26桁（長い）
    [InlineData("ZZZZB42318A0038000001231")]  // 非hex
    public void DeriveSearchKey_InvalidEpc_Throws(string epcHex)
    {
        Assert.Throws<ArgumentException>(() => Sgtin96.DeriveSearchKey(epcHex));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~Sgtin96Tests`
Expected: コンパイル失敗（`Sgtin96` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Epc/Sgtin96.cs
namespace EpcForwarder.Core.Epc;

/// <summary>SGTIN-96 のhex入口。詳細は docs/design/epc-mask.md。</summary>
public static class Sgtin96
{
    /// <summary>SGTIN-96 EPCのバイト長（96bit = 12バイト = 24 hex桁）。</summary>
    public const int HexLength = 24;

    /// <summary>EPC(hex文字列)を検証し、検索キー(hex,大文字)を返す。不正時は ArgumentException。</summary>
    public static string DeriveSearchKey(string epcHex)
    {
        ArgumentNullException.ThrowIfNull(epcHex);
        if (epcHex.Length != HexLength)
        {
            throw new ArgumentException($"SGTIN-96 EPC must be {HexLength} hex chars, got {epcHex.Length}.", nameof(epcHex));
        }

        byte[] epc;
        try
        {
            epc = Convert.FromHexString(epcHex);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("EPC is not valid hex.", nameof(epcHex), ex);
        }

        var key = EpcKey.Derive(epc, EpcKey.Sgtin96Mask);
        return Convert.ToHexString(key);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~Sgtin96Tests`
Expected: PASS（5ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Epc/Sgtin96.cs tests/EpcForwarder.Core.Tests/Epc/Sgtin96Tests.cs
git commit -m "feat(core): SGTIN-96 検索キー導出(hex入口)"
```

---

## Task 2: Session 集約と状態遷移

**Files:**
- Create: `src/EpcForwarder.Core/Sessions/SessionStatus.cs`, `src/EpcForwarder.Core/Sessions/SessionType.cs`, `src/EpcForwarder.Core/Sessions/Session.cs`
- Test: `tests/EpcForwarder.Core.Tests/Sessions/SessionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Sessions/SessionTests.cs
using EpcForwarder.Core.Sessions;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class SessionTests
{
    private static Session NewOpen() =>
        new(Guid.NewGuid(), tenantId: 1, SessionType.Shipment, businessKey: "DN-1",
            now: DateTimeOffset.UnixEpoch);

    [Fact]
    public void New_Session_IsOpen()
    {
        Assert.Equal(SessionStatus.Open, NewOpen().Status);
    }

    [Fact]
    public void Finalize_FromOpen_Succeeds()
    {
        var s = NewOpen();
        s.Finalize(DateTimeOffset.UnixEpoch);
        Assert.Equal(SessionStatus.Finalized, s.Status);
        Assert.NotNull(s.FinalizedAt);
    }

    [Fact]
    public void MarkForwarded_RequiresFinalized()
    {
        var s = NewOpen();
        Assert.Throws<InvalidOperationException>(() => s.MarkForwarded(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void MarkForwarded_AfterFinalize_Succeeds()
    {
        var s = NewOpen();
        s.Finalize(DateTimeOffset.UnixEpoch);
        s.MarkForwarded(DateTimeOffset.UnixEpoch);
        Assert.Equal(SessionStatus.Forwarded, s.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~SessionTests`
Expected: コンパイル失敗（型未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Sessions/SessionStatus.cs
namespace EpcForwarder.Core.Sessions;

public enum SessionStatus { Open, Finalized, Forwarded, Archived, Purged }
```

```csharp
// src/EpcForwarder.Core/Sessions/SessionType.cs
namespace EpcForwarder.Core.Sessions;

public enum SessionType { Shipment, Inventory }
```

```csharp
// src/EpcForwarder.Core/Sessions/Session.cs
namespace EpcForwarder.Core.Sessions;

/// <summary>セッション集約。状態遷移 open→finalized→forwarded を強制する。</summary>
public sealed class Session
{
    public Guid PublicId { get; }
    public int TenantId { get; }
    public SessionType Type { get; }
    public string? BusinessKey { get; }
    public SessionStatus Status { get; private set; }
    public int? ExpectedCount { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastEventAt { get; private set; }
    public DateTimeOffset? FinalizedAt { get; private set; }
    public DateTimeOffset? ForwardedAt { get; private set; }

    public Session(Guid publicId, int tenantId, SessionType type, string? businessKey, DateTimeOffset now)
    {
        PublicId = publicId;
        TenantId = tenantId;
        Type = type;
        BusinessKey = businessKey;
        Status = SessionStatus.Open;
        CreatedAt = now;
        LastEventAt = now;
    }

    public void Touch(DateTimeOffset now) => LastEventAt = now;

    public void SetExpectedCount(int count) => ExpectedCount = count;

    public void Finalize(DateTimeOffset now)
    {
        if (Status != SessionStatus.Open)
        {
            throw new InvalidOperationException($"Cannot finalize a session in status {Status}.");
        }

        Status = SessionStatus.Finalized;
        FinalizedAt = now;
        LastEventAt = now;
    }

    public void MarkForwarded(DateTimeOffset now)
    {
        if (Status != SessionStatus.Finalized)
        {
            throw new InvalidOperationException($"Cannot mark forwarded from status {Status}.");
        }

        Status = SessionStatus.Forwarded;
        ForwardedAt = now;
        LastEventAt = now;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~SessionTests`
Expected: PASS（4ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Sessions/SessionStatus.cs src/EpcForwarder.Core/Sessions/SessionType.cs src/EpcForwarder.Core/Sessions/Session.cs tests/EpcForwarder.Core.Tests/Sessions/SessionTests.cs
git commit -m "feat(core): Session集約と状態遷移"
```

---

## Task 3: ポート定義と送信モデル

定義のみ（振る舞いなし）。ビルドが通ることで検証する。

**Files:**
- Create: `src/EpcForwarder.Core/Abstractions/Ports.cs`, `src/EpcForwarder.Core/Delivery/WebhookModels.cs`

- [ ] **Step 1: Write the port definitions**

```csharp
// src/EpcForwarder.Core/Abstractions/Ports.cs
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface IIdGenerator { Guid NewGuid(); }

public interface ISessionStore
{
    Session? Get(Guid publicId);
    void Save(Session session);
}

/// <summary>1セッション内の読取1件。EPC単位で後勝ち。</summary>
public sealed record ReadingEntry(string Epc, string? SearchKey, string? DeviceId, DateTimeOffset ReadAt);

public interface IReadingStore
{
    void Upsert(Guid sessionId, ReadingEntry entry); // EPC一致なら上書き（後勝ち）
    IReadOnlyList<ReadingEntry> List(Guid sessionId);
    int CountUnique(Guid sessionId);
}

public interface IProductCatalog
{
    /// <summary>検索キー(hex)からSKUを解決。未登録は null。</summary>
    string? ResolveSku(int tenantId, string searchKey);
}

public sealed record SnapshotRecord(Guid SessionId, int Version, bool IsFinal, Guid IdempotencyKey, int ItemCount, bool Success);

public interface ISnapshotStore
{
    int NextVersion(Guid sessionId); // セッション内で単調増加
    void Record(SnapshotRecord record);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string name, CancellationToken ct = default);
}

public sealed record WebhookRequest(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string Body);

public sealed record WebhookResult(bool Success, int StatusCode);

public interface IWebhookSender
{
    Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default);
}

public sealed record ReachabilityResult(int Expected, int Received)
{
    public bool IsMatch => Expected == Received;
    public int Missing => Expected - Received;
}

public interface IDeviceFeedback
{
    Task SendReachabilityAsync(Guid sessionId, ReachabilityResult result, CancellationToken ct = default);
}
```

```csharp
// src/EpcForwarder.Core/Delivery/WebhookModels.cs
namespace EpcForwarder.Core.Delivery;

public sealed record AggregateItem(string Sku, int Quantity);

public sealed record UnknownTags(int Count, IReadOnlyList<string> Epcs);

public sealed record WebhookEnvelope(
    string SchemaVersion,
    string Tenant,
    Guid SessionId,
    string? BusinessKey,
    string Type,
    int SnapshotVersion,
    bool IsFinal,
    Guid IdempotencyKey,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<AggregateItem> Items,
    UnknownTags UnknownTags);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build EpcForwarder.sln --nologo -v q`
Expected: 0 警告 / 0 エラー。

- [ ] **Step 3: Commit**

```bash
git add src/EpcForwarder.Core/Abstractions/Ports.cs src/EpcForwarder.Core/Delivery/WebhookModels.cs
git commit -m "feat(core): 外部依存ポートと送信モデルを定義"
```

---

## Task 4: SKU集約（純関数）

**Files:**
- Create: `src/EpcForwarder.Core/Delivery/SkuAggregator.cs`
- Test: `tests/EpcForwarder.Core.Tests/Delivery/SkuAggregatorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Delivery/SkuAggregatorTests.cs
using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class SkuAggregatorTests
{
    [Fact]
    public void Aggregate_CountsBySku_SortedByOrdinal()
    {
        var result = SkuAggregator.Aggregate(new[] { "ITEM-BBB", "ITEM-AAA", "ITEM-AAA" });

        Assert.Collection(result,
            i => { Assert.Equal("ITEM-AAA", i.Sku); Assert.Equal(2, i.Quantity); },
            i => { Assert.Equal("ITEM-BBB", i.Sku); Assert.Equal(1, i.Quantity); });
    }

    [Fact]
    public void Aggregate_Empty_ReturnsEmpty()
    {
        Assert.Empty(SkuAggregator.Aggregate(Array.Empty<string>()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~SkuAggregatorTests`
Expected: コンパイル失敗（`SkuAggregator` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Delivery/SkuAggregator.cs
namespace EpcForwarder.Core.Delivery;

public static class SkuAggregator
{
    public static IReadOnlyList<AggregateItem> Aggregate(IEnumerable<string> skus) =>
        skus.GroupBy(s => s)
            .Select(g => new AggregateItem(g.Key, g.Count()))
            .OrderBy(i => i.Sku, StringComparer.Ordinal)
            .ToList();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~SkuAggregatorTests`
Expected: PASS（2ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Delivery/SkuAggregator.cs tests/EpcForwarder.Core.Tests/Delivery/SkuAggregatorTests.cs
git commit -m "feat(core): SKU集約(純関数)"
```

---

## Task 5: HMAC署名

**Files:**
- Create: `src/EpcForwarder.Core/Delivery/HmacSigner.cs`
- Test: `tests/EpcForwarder.Core.Tests/Delivery/HmacSignerTests.cs`

- [ ] **Step 1: Write the failing test**

正確なダイジェスト値はハードコードせず、契約上の性質（prefix・長さ・決定性・入力鋭敏性）を検証する。

```csharp
// tests/EpcForwarder.Core.Tests/Delivery/HmacSignerTests.cs
using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class HmacSignerTests
{
    [Fact]
    public void Sign_Format_IsSha256PrefixedHex()
    {
        var sig = HmacSigner.Sign("secret", "2026-06-18T00:00:00Z", "{}");

        Assert.StartsWith("sha256=", sig);
        var hex = sig["sha256=".Length..];
        Assert.Equal(64, hex.Length); // SHA-256 = 32バイト = 64 hex
        Assert.Matches("^[0-9a-f]{64}$", hex);
    }

    [Fact]
    public void Sign_IsDeterministic()
    {
        Assert.Equal(
            HmacSigner.Sign("k", "t", "body"),
            HmacSigner.Sign("k", "t", "body"));
    }

    [Theory]
    [InlineData("k2", "t", "body")]
    [InlineData("k", "t2", "body")]
    [InlineData("k", "t", "body2")]
    public void Sign_DiffersWhenAnyInputChanges(string secret, string ts, string body)
    {
        Assert.NotEqual(
            HmacSigner.Sign("k", "t", "body"),
            HmacSigner.Sign(secret, ts, body));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~HmacSignerTests`
Expected: コンパイル失敗（`HmacSigner` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Delivery/HmacSigner.cs
using System.Security.Cryptography;
using System.Text;

namespace EpcForwarder.Core.Delivery;

/// <summary>HMAC-SHA256(secret, timestamp + "." + body)。詳細は docs/design/webhook-contract.md §4。</summary>
public static class HmacSigner
{
    public static string Sign(string secret, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~HmacSignerTests`
Expected: PASS（5ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Delivery/HmacSigner.cs tests/EpcForwarder.Core.Tests/Delivery/HmacSignerTests.cs
git commit -m "feat(core): HMAC-SHA256署名"
```

---

## Task 6: 送信先URLガード（SSRF）

**Files:**
- Create: `src/EpcForwarder.Core/Delivery/WebhookUrlGuard.cs`
- Test: `tests/EpcForwarder.Core.Tests/Delivery/WebhookUrlGuardTests.cs`

- [ ] **Step 1: Write the failing test**

DNS解決はポート `IHostResolver` で抽象化し、テストはフェイクで解決IPを与える。

```csharp
// tests/EpcForwarder.Core.Tests/Delivery/WebhookUrlGuardTests.cs
using System.Net;
using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class WebhookUrlGuardTests
{
    private sealed class FakeResolver(params string[] ips) : IHostResolver
    {
        public IReadOnlyList<IPAddress> Resolve(string host) =>
            ips.Select(IPAddress.Parse).ToList();
    }

    private static readonly WebhookUrlGuardOptions Strict = new(AllowHttp: false, AllowPrivateNetworks: false);

    [Fact]
    public void Validate_PublicHttps_Ok()
    {
        WebhookUrlGuard.Validate("https://api.example.com/hook", Strict, new FakeResolver("93.184.216.34"));
    }

    [Fact]
    public void Validate_Http_WhenNotAllowed_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebhookUrlGuard.Validate("http://api.example.com/hook", Strict, new FakeResolver("93.184.216.34")));
    }

    [Theory]
    [InlineData("169.254.169.254")] // メタデータ
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    public void Validate_PrivateOrMetadata_Throws(string ip)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebhookUrlGuard.Validate("https://internal.example.com/hook", Strict, new FakeResolver(ip)));
    }

    [Fact]
    public void Validate_PrivateAllowed_Ok()
    {
        var opt = new WebhookUrlGuardOptions(AllowHttp: true, AllowPrivateNetworks: true);
        WebhookUrlGuard.Validate("http://127.0.0.1:5000/hook", opt, new FakeResolver("127.0.0.1"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~WebhookUrlGuardTests`
Expected: コンパイル失敗（型未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Delivery/WebhookUrlGuard.cs
using System.Net;
using System.Net.Sockets;

namespace EpcForwarder.Core.Delivery;

public interface IHostResolver
{
    IReadOnlyList<IPAddress> Resolve(string host);
}

public sealed record WebhookUrlGuardOptions(bool AllowHttp, bool AllowPrivateNetworks);

/// <summary>送信先URLのSSRFガード。詳細は docs/design/webhook-contract.md §7。</summary>
public static class WebhookUrlGuard
{
    public static void Validate(string url, WebhookUrlGuardOptions options, IHostResolver resolver)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid URL: {url}");
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isHttp = uri.Scheme == Uri.UriSchemeHttp;
        if (!isHttps && !(isHttp && options.AllowHttp))
        {
            throw new InvalidOperationException($"URL scheme not allowed: {uri.Scheme}");
        }

        if (options.AllowPrivateNetworks)
        {
            return;
        }

        foreach (var ip in resolver.Resolve(uri.Host))
        {
            if (IsBlocked(ip))
            {
                throw new InvalidOperationException($"Destination resolves to a blocked address: {ip}");
            }
        }
    }

    private static bool IsBlocked(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10/8, 172.16/12, 192.168/16, 169.254/16
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique-local
            return false;
        }

        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~WebhookUrlGuardTests`
Expected: PASS（7ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Delivery/WebhookUrlGuard.cs tests/EpcForwarder.Core.Tests/Delivery/WebhookUrlGuardTests.cs
git commit -m "feat(core): 送信先URLガード(SSRF)"
```

---

## Task 7: ペイロード組立（snake_case JSON）

**Files:**
- Create: `src/EpcForwarder.Core/Delivery/PayloadBuilder.cs`
- Test: `tests/EpcForwarder.Core.Tests/Delivery/PayloadBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Delivery/PayloadBuilderTests.cs
using EpcForwarder.Core.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class PayloadBuilderTests
{
    [Fact]
    public void Serialize_UsesSnakeCase_AndContainsItems()
    {
        var envelope = new WebhookEnvelope(
            SchemaVersion: "1",
            Tenant: "acme",
            SessionId: Guid.Parse("9c3a8f10-0000-0000-0000-000000000000"),
            BusinessKey: "DN-2026-000123",
            Type: "shipment",
            SnapshotVersion: 3,
            IsFinal: true,
            IdempotencyKey: Guid.Parse("f1e2d3c4-0000-0000-0000-000000000000"),
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Items: new[] { new AggregateItem("ITEM-AAA", 2) },
            UnknownTags: new UnknownTags(0, Array.Empty<string>()));

        var json = new PayloadBuilder().Serialize(envelope);

        Assert.Contains("\"schema_version\":\"1\"", json);
        Assert.Contains("\"snapshot_version\":3", json);
        Assert.Contains("\"is_final\":true", json);
        Assert.Contains("\"idempotency_key\":\"f1e2d3c4", json);
        Assert.Contains("\"sku\":\"ITEM-AAA\"", json);
        Assert.Contains("\"quantity\":2", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~PayloadBuilderTests`
Expected: コンパイル失敗（`PayloadBuilder` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Delivery/PayloadBuilder.cs
using System.Text.Json;

namespace EpcForwarder.Core.Delivery;

/// <summary>WebhookEnvelope を snake_case JSON へ直列化。詳細は docs/design/webhook-contract.md §3。</summary>
public sealed class PayloadBuilder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public string Serialize(WebhookEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~PayloadBuilderTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Delivery/PayloadBuilder.cs tests/EpcForwarder.Core.Tests/Delivery/PayloadBuilderTests.cs
git commit -m "feat(core): ペイロード組立(snake_case JSON)"
```

---

## Task 8: テスト用 in-memory フェイク群

以降のサービステストで使う共有フェイク。フェイク自身に検証ロジックはないが、後勝ちUPSERTの挙動を1件テストで固定する。

**Files:**
- Create: `tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs`
- Test: `tests/EpcForwarder.Core.Tests/Fakes/InMemoryReadingStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Fakes/InMemoryReadingStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Fakes;

public class InMemoryReadingStoreTests
{
    [Fact]
    public void Upsert_SameEpc_LastWriteWins()
    {
        var store = new InMemoryReadingStore();
        var s = Guid.NewGuid();
        store.Upsert(s, new ReadingEntry("EPC1", "K1", "devA", DateTimeOffset.UnixEpoch));
        store.Upsert(s, new ReadingEntry("EPC1", "K1", "devB", DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.Equal(1, store.CountUnique(s));
        Assert.Equal("devB", store.List(s).Single().DeviceId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~InMemoryReadingStoreTests`
Expected: コンパイル失敗（フェイク未定義）。

- [ ] **Step 3: Write the fakes**

```csharp
// tests/EpcForwarder.Core.Tests/Fakes/InMemoryFakes.cs
using System.Collections.Concurrent;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;

namespace EpcForwarder.Core.Tests.Fakes;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _n;
    public Guid NewGuid() => new($"00000000-0000-0000-0000-{(++_n):D12}");
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<Guid, Session> _map = new();
    public Session? Get(Guid publicId) => _map.GetValueOrDefault(publicId);
    public void Save(Session session) => _map[session.PublicId] = session;
}

public sealed class InMemoryReadingStore : IReadingStore
{
    private readonly ConcurrentDictionary<Guid, Dictionary<string, ReadingEntry>> _map = new();

    public void Upsert(Guid sessionId, ReadingEntry entry)
    {
        var bag = _map.GetOrAdd(sessionId, _ => new Dictionary<string, ReadingEntry>());
        lock (bag) { bag[entry.Epc] = entry; }
    }

    public IReadOnlyList<ReadingEntry> List(Guid sessionId) =>
        _map.TryGetValue(sessionId, out var bag) ? bag.Values.ToList() : Array.Empty<ReadingEntry>();

    public int CountUnique(Guid sessionId) =>
        _map.TryGetValue(sessionId, out var bag) ? bag.Count : 0;
}

public sealed class InMemoryProductCatalog : IProductCatalog
{
    private readonly Dictionary<(int, string), string> _map = new();
    public void Add(int tenantId, string searchKey, string sku) => _map[(tenantId, searchKey)] = sku;
    public string? ResolveSku(int tenantId, string searchKey) => _map.GetValueOrDefault((tenantId, searchKey));
}

public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly Dictionary<Guid, int> _versions = new();
    public List<SnapshotRecord> Records { get; } = new();
    public int NextVersion(Guid sessionId)
    {
        var v = _versions.GetValueOrDefault(sessionId) + 1;
        _versions[sessionId] = v;
        return v;
    }
    public void Record(SnapshotRecord record) => Records.Add(record);
}

public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _map = new();
    public void Add(string name, string value) => _map[name] = value;
    public Task<string?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_map.GetValueOrDefault(name));
}

public sealed class CapturingWebhookSender : IWebhookSender
{
    public WebhookRequest? Last { get; private set; }
    public WebhookResult Next { get; set; } = new(true, 200);
    public Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        Last = request;
        return Task.FromResult(Next);
    }
}

public sealed class CapturingDeviceFeedback : IDeviceFeedback
{
    public List<(Guid SessionId, ReachabilityResult Result)> Sent { get; } = new();
    public Task SendReachabilityAsync(Guid sessionId, ReachabilityResult result, CancellationToken ct = default)
    {
        Sent.Add((sessionId, result));
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~InMemoryReadingStoreTests`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add tests/EpcForwarder.Core.Tests/Fakes/
git commit -m "test(core): in-memoryフェイク群"
```

---

## Task 9: ReadingIngestor（後勝ち取込）

**Files:**
- Create: `src/EpcForwarder.Core/Sessions/ReadingIngestor.cs`
- Test: `tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class ReadingIngestorTests
{
    [Fact]
    public void Ingest_ResolvesSearchKey_AndStores()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var sut = new ReadingIngestor(sessions, readings, clock);

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));

        sut.Ingest(id, "302DB42318A0038000001231", "devA", clock.UtcNow, resolveSku: true);

        var entry = Assert.Single(readings.List(id));
        Assert.Equal(Sgtin96.DeriveSearchKey("302DB42318A0038000001231"), entry.SearchKey);
    }

    [Fact]
    public void Ingest_UnknownSession_Throws()
    {
        var sut = new ReadingIngestor(new InMemorySessionStore(), new InMemoryReadingStore(),
            new FixedClock(DateTimeOffset.UnixEpoch));

        Assert.Throws<InvalidOperationException>(() =>
            sut.Ingest(Guid.NewGuid(), "302DB42318A0038000001231", "devA", DateTimeOffset.UnixEpoch, true));
    }

    [Fact]
    public void Ingest_RawMode_LeavesSearchKeyNull()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var sut = new ReadingIngestor(sessions, readings, clock);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));

        sut.Ingest(id, "FREEFORM-EPC", "devA", clock.UtcNow, resolveSku: false);

        Assert.Null(readings.List(id).Single().SearchKey);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ReadingIngestorTests`
Expected: コンパイル失敗（`ReadingIngestor` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Sessions/ReadingIngestor.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Epc;

namespace EpcForwarder.Core.Sessions;

/// <summary>読取1件をセッションへ取り込む。SKU解決時は EPC&Mask で検索キーを付与。後勝ちはストアが担う。</summary>
public sealed class ReadingIngestor(ISessionStore sessions, IReadingStore readings, IClock clock)
{
    public void Ingest(Guid sessionId, string epcHex, string? deviceId, DateTimeOffset readAt, bool resolveSku)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        var searchKey = resolveSku ? Sgtin96.DeriveSearchKey(epcHex) : null;
        readings.Upsert(sessionId, new ReadingEntry(epcHex, searchKey, deviceId, readAt));

        session.Touch(clock.UtcNow);
        sessions.Save(session);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ReadingIngestorTests`
Expected: PASS（3ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Sessions/ReadingIngestor.cs tests/EpcForwarder.Core.Tests/Sessions/ReadingIngestorTests.cs
git commit -m "feat(core): ReadingIngestor(後勝ち取込)"
```

---

## Task 10: ShipmentReconciler（完了＋到達性突合）

**Files:**
- Create: `src/EpcForwarder.Core/Sessions/ShipmentReconciler.cs`
- Test: `tests/EpcForwarder.Core.Tests/Sessions/ShipmentReconcilerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Sessions/ShipmentReconcilerTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Sessions;

public class ShipmentReconcilerTests
{
    private static (ShipmentReconciler Sut, Guid Id, InMemoryReadingStore Readings, CapturingDeviceFeedback Fb)
        Build(int receivedCount)
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var fb = new CapturingDeviceFeedback();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));
        for (var i = 0; i < receivedCount; i++)
        {
            readings.Upsert(id, new ReadingEntry($"EPC{i}", $"K{i}", "devA", clock.UtcNow));
        }
        return (new ShipmentReconciler(sessions, readings, fb, clock), id, readings, fb);
    }

    [Fact]
    public async Task Complete_Match_NoFeedback()
    {
        var (sut, id, _, fb) = Build(receivedCount: 3);

        var result = await sut.CompleteAsync(id, expectedCount: 3);

        Assert.True(result.IsMatch);
        Assert.Empty(fb.Sent);
    }

    [Fact]
    public async Task Complete_Mismatch_SendsReReadFeedback()
    {
        var (sut, id, _, fb) = Build(receivedCount: 3);

        var result = await sut.CompleteAsync(id, expectedCount: 5);

        Assert.False(result.IsMatch);
        Assert.Equal(2, result.Missing);
        var sent = Assert.Single(fb.Sent);
        Assert.Equal(id, sent.SessionId);
        Assert.Equal(2, sent.Result.Missing);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ShipmentReconcilerTests`
Expected: コンパイル失敗（`ShipmentReconciler` 未定義）。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/EpcForwarder.Core/Sessions/ShipmentReconciler.cs
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Core.Sessions;

/// <summary>伝票完了イベントの到達性突合（システム到達性検証）。不一致時のみ再読取をフィードバック。</summary>
public sealed class ShipmentReconciler(
    ISessionStore sessions,
    IReadingStore readings,
    IDeviceFeedback feedback,
    IClock clock)
{
    public async Task<ReachabilityResult> CompleteAsync(Guid sessionId, int expectedCount, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        session.SetExpectedCount(expectedCount);
        var received = readings.CountUnique(sessionId);
        var result = new ReachabilityResult(expectedCount, received);

        if (!result.IsMatch)
        {
            await feedback.SendReachabilityAsync(sessionId, result, ct);
        }

        session.Touch(clock.UtcNow);
        sessions.Save(session);
        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ShipmentReconcilerTests`
Expected: PASS（2ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Sessions/ShipmentReconciler.cs tests/EpcForwarder.Core.Tests/Sessions/ShipmentReconcilerTests.cs
git commit -m "feat(core): ShipmentReconciler(到達性突合)"
```

---

## Task 11: ShipmentDeliverer（確定→集約→送信→記録）

**Files:**
- Create: `src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs`
- Test: `tests/EpcForwarder.Core.Tests/Delivery/ShipmentDelivererTests.cs`

`DeliveryTarget` は宛先設定（data-model `destination`）の実行時投影。HMAC鍵・カスタムヘッダ値は `ISecretStore` で解決する。

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Delivery/ShipmentDelivererTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using Xunit;

namespace EpcForwarder.Core.Tests.Delivery;

public class ShipmentDelivererTests
{
    private const string EpcA = "302DB42318A0038000001231";
    private const string EpcB = "302DB42318A0038000009999"; // 同一商品(同一検索キー)別シリアル

    [Fact]
    public async Task FinalizeAndDeliver_Aggregates_Signs_Records_AndForwards()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snapshots = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        secrets.Add("hook-hmac", "topsecret");

        var key = Sgtin96.DeriveSearchKey(EpcA); // EpcA/EpcB は同一検索キー
        products.Add(1, key, "ITEM-AAA");

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));
        readings.Upsert(id, new ReadingEntry(EpcA, key, "devA", clock.UtcNow));
        readings.Upsert(id, new ReadingEntry(EpcB, key, "devA", clock.UtcNow));

        var sut = new ShipmentDeliverer(sessions, readings, products, snapshots, sender, secrets,
            new PayloadBuilder(), clock, ids);

        var target = new DeliveryTarget(
            Url: "https://api.example.com/hook",
            Method: "POST",
            SchemaVersion: "1",
            HmacEnabled: true,
            HmacSecretRef: "hook-hmac",
            Headers: new Dictionary<string, string>());

        var result = await sut.FinalizeAndDeliverAsync(id, target);

        Assert.True(result.Success);
        // セッションは forwarded
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        // スナップショット記録
        var snap = Assert.Single(snapshots.Records);
        Assert.True(snap.IsFinal);
        Assert.Equal(1, snap.Version);
        // 送信内容
        var req = sender.Last!;
        Assert.Contains("\"sku\":\"ITEM-AAA\"", req.Body);
        Assert.Contains("\"quantity\":2", req.Body); // EpcA+EpcB が同一SKUに集約
        Assert.True(req.Headers.ContainsKey("Idempotency-Key"));
        Assert.Equal("true", req.Headers["X-EPCF-Is-Final"]);
        Assert.True(req.Headers.ContainsKey("X-EPCF-Signature"));
    }

    [Fact]
    public async Task FinalizeAndDeliver_UnknownTags_GoToSeparateLane()
    {
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog(); // 何も登録しない → 全部未知
        var snapshots = new InMemorySnapshotStore();
        var sender = new CapturingWebhookSender();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();

        var key = Sgtin96.DeriveSearchKey(EpcA);
        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));
        readings.Upsert(id, new ReadingEntry(EpcA, key, "devA", clock.UtcNow));

        var sut = new ShipmentDeliverer(sessions, readings, products, snapshots, sender, secrets,
            new PayloadBuilder(), clock, ids);

        var target = new DeliveryTarget("https://api.example.com/hook", "POST", "1", false, null,
            new Dictionary<string, string>());

        await sut.FinalizeAndDeliverAsync(id, target);

        var req = sender.Last!;
        Assert.Contains("\"items\":[]", req.Body);
        Assert.Contains("\"count\":1", req.Body);       // unknown_tags.count
        Assert.DoesNotContain("X-EPCF-Signature", string.Join(",", req.Headers.Keys)); // HMAC無効
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ShipmentDelivererTests`
Expected: コンパイル失敗（`ShipmentDeliverer`/`DeliveryTarget` 未定義）。

- [ ] **Step 3: Write minimal implementation**

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

/// <summary>伝票の確定→SKU集約→ペイロード組立→署名→送信→スナップショット記録→forwarded。</summary>
public sealed class ShipmentDeliverer(
    ISessionStore sessions,
    IReadingStore readings,
    IProductCatalog products,
    ISnapshotStore snapshots,
    IWebhookSender sender,
    ISecretStore secrets,
    PayloadBuilder payloadBuilder,
    IClock clock,
    IIdGenerator ids)
{
    public async Task<WebhookResult> FinalizeAndDeliverAsync(Guid sessionId, DeliveryTarget target, CancellationToken ct = default)
    {
        var session = sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId}");

        session.Finalize(clock.UtcNow);

        var resolved = new List<string>();
        var unknown = new List<string>();
        foreach (var entry in readings.List(sessionId))
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
        var version = snapshots.NextVersion(sessionId);
        var idempotencyKey = ids.NewGuid();
        var generatedAt = clock.UtcNow;

        var envelope = new WebhookEnvelope(
            SchemaVersion: target.SchemaVersion,
            Tenant: session.TenantId.ToString(),
            SessionId: session.PublicId,
            BusinessKey: session.BusinessKey,
            Type: "shipment",
            SnapshotVersion: version,
            IsFinal: true,
            IdempotencyKey: idempotencyKey,
            GeneratedAt: generatedAt,
            Items: items,
            UnknownTags: new UnknownTags(unknown.Count, unknown));

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
            ["X-EPCF-Is-Final"] = "true",
            ["X-EPCF-Timestamp"] = timestamp,
        };

        foreach (var (name, secretRef) in target.Headers)
        {
            var value = await secrets.GetAsync(secretRef, ct);
            if (value is not null)
            {
                headers[name] = value;
            }
        }

        if (target.HmacEnabled && target.HmacSecretRef is not null)
        {
            var key = await secrets.GetAsync(target.HmacSecretRef, ct);
            if (key is not null)
            {
                headers["X-EPCF-Signature"] = HmacSigner.Sign(key, timestamp, body);
            }
        }

        var result = await sender.SendAsync(new WebhookRequest(target.Url, target.Method, headers, body), ct);
        snapshots.Record(new SnapshotRecord(sessionId, version, IsFinal: true, idempotencyKey, items.Count, result.Success));

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

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ShipmentDelivererTests`
Expected: PASS（2ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Core/Delivery/ShipmentDeliverer.cs tests/EpcForwarder.Core.Tests/Delivery/ShipmentDelivererTests.cs
git commit -m "feat(core): ShipmentDeliverer(確定→集約→署名→送信→記録)"
```

---

## Task 12: HttpWebhookSender（実HTTP送信・Infrastructure）

`Infrastructure` にはテストプロジェクト参照を追加し、`HttpListener` で受信検証する。

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs`
- Modify: `tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj`（Infrastructure参照を追加）
- Test: `tests/EpcForwarder.Core.Tests/Infrastructure/HttpWebhookSenderTests.cs`

- [ ] **Step 1: Add Infrastructure reference to the test project**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add tests/EpcForwarder.Core.Tests reference src/EpcForwarder.Infrastructure
```
Expected: 参照追加成功。

- [ ] **Step 2: Write the failing test**

```csharp
// tests/EpcForwarder.Core.Tests/Infrastructure/HttpWebhookSenderTests.cs
using System.Net;
using System.Text;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests.Infrastructure;

public class HttpWebhookSenderTests
{
    [Fact]
    public async Task SendAsync_PostsBodyAndHeaders_ReturnsSuccess()
    {
        using var listener = new HttpListener();
        var prefix = "http://127.0.0.1:18791/hook/"; // 非特権ポート(>1024)
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? receivedBody = null;
        string? receivedIdem = null;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            receivedBody = await reader.ReadToEndAsync();
            receivedIdem = ctx.Request.Headers["Idempotency-Key"];
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        using var client = new HttpClient();
        var sut = new HttpWebhookSender(client);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Idempotency-Key"] = "abc-123",
        };

        var result = await sut.SendAsync(new WebhookRequest(prefix, "POST", headers, "{\"hello\":1}"));

        await serverTask;
        listener.Stop();

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("{\"hello\":1}", receivedBody);
        Assert.Equal("abc-123", receivedIdem);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~HttpWebhookSenderTests`
Expected: コンパイル失敗（`HttpWebhookSender` 未定義）。

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs
using System.Net.Http.Headers;
using System.Text;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Delivery;

/// <summary>IWebhookSender の実HTTP実装。URLガードは上位(アプリ層)で実施済みの前提。</summary>
public sealed class HttpWebhookSender(HttpClient client) : IWebhookSender
{
    public async Task<WebhookResult> SendAsync(WebhookRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);

        var contentType = request.Headers.TryGetValue("Content-Type", out var ctv)
            ? ctv
            : "application/json; charset=utf-8";
        message.Content = new StringContent(request.Body, Encoding.UTF8);
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Content側で設定済み
            }

            message.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(message, ct);
        return new WebhookResult(response.IsSuccessStatusCode, (int)response.StatusCode);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~HttpWebhookSenderTests`
Expected: PASS。

- [ ] **Step 6: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Delivery/HttpWebhookSender.cs tests/EpcForwarder.Core.Tests/Infrastructure/HttpWebhookSenderTests.cs tests/EpcForwarder.Core.Tests/EpcForwarder.Core.Tests.csproj
git commit -m "feat(infra): HttpWebhookSender(実HTTP送信)"
```

---

## Task 13: インプロセスE2E（取込→完了→確定→実HTTP受信）

全部品を結線し、伝票1本の流れを実HTTPで通す。URLガードはローカル受信のため `AllowPrivateNetworks: true` で明示的に許可する（本番はfalse運用）。

**Files:**
- Test: `tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs`

- [ ] **Step 1: Write the E2E test**

```csharp
// tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs
using System.Net;
using System.Text;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Epc;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Core.Tests.Fakes;
using EpcForwarder.Infrastructure.Delivery;
using Xunit;

namespace EpcForwarder.Core.Tests;

public class ShipmentE2ETests
{
    private sealed class LoopbackResolver : IHostResolver
    {
        public IReadOnlyList<IPAddress> Resolve(string host) => new[] { IPAddress.Loopback };
    }

    [Fact]
    public async Task Shipment_Ingest_Complete_Finalize_DeliversAggregatedPayload()
    {
        // --- 受信側(連携先)スタブ ---
        using var listener = new HttpListener();
        const string url = "http://127.0.0.1:8780/hook/";
        listener.Prefixes.Add(url);
        listener.Start();
        string? body = null;
        var server = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        // --- 組み立て ---
        var sessions = new InMemorySessionStore();
        var readings = new InMemoryReadingStore();
        var products = new InMemoryProductCatalog();
        var snapshots = new InMemorySnapshotStore();
        var secrets = new FakeSecretStore();
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var ids = new SequentialIdGenerator();
        using var http = new HttpClient();

        var ingestor = new ReadingIngestor(sessions, readings, clock);
        var reconciler = new ShipmentReconciler(sessions, readings, new CapturingDeviceFeedback(), clock);
        var deliverer = new ShipmentDeliverer(sessions, readings, products, snapshots,
            new HttpWebhookSender(http), secrets, new PayloadBuilder(), clock, ids);

        const string epcA = "302DB42318A0038000001231";
        const string epcB = "302DB42318A0038000009999"; // 同一検索キー
        var key = Sgtin96.DeriveSearchKey(epcA);
        products.Add(1, key, "ITEM-AAA");

        var id = Guid.NewGuid();
        sessions.Save(new Session(id, 1, SessionType.Shipment, "DN-1", clock.UtcNow));

        // --- フロー ---
        ingestor.Ingest(id, epcA, "devA", clock.UtcNow, resolveSku: true);
        ingestor.Ingest(id, epcB, "devA", clock.UtcNow, resolveSku: true);

        var reach = await reconciler.CompleteAsync(id, expectedCount: 2);
        Assert.True(reach.IsMatch);

        // 送信前にURLガード（ローカル許可）
        WebhookUrlGuard.Validate(url, new WebhookUrlGuardOptions(AllowHttp: true, AllowPrivateNetworks: true), new LoopbackResolver());

        var target = new DeliveryTarget(url, "POST", "1", HmacEnabled: false, HmacSecretRef: null,
            Headers: new Dictionary<string, string>());
        var result = await deliverer.FinalizeAndDeliverAsync(id, target);

        await server;
        listener.Stop();

        // --- 検証 ---
        Assert.True(result.Success);
        Assert.Equal(SessionStatus.Forwarded, sessions.Get(id)!.Status);
        Assert.NotNull(body);
        Assert.Contains("\"sku\":\"ITEM-AAA\"", body);
        Assert.Contains("\"quantity\":2", body);
        Assert.Contains("\"is_final\":true", body);
    }
}
```

- [ ] **Step 2: Run the E2E test**

Run: `dotnet test EpcForwarder.sln --nologo --filter FullyQualifiedName~ShipmentE2ETests`
Expected: PASS。

- [ ] **Step 3: Run the full suite**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: 全テスト PASS、0 失敗。

- [ ] **Step 4: Commit**

```bash
git add tests/EpcForwarder.Core.Tests/ShipmentE2ETests.cs
git commit -m "test: 伝票インプロセスE2E(取込→完了→確定→実HTTP)"
```

---

## 完了条件

- `dotnet build` 0警告/0エラー、`dotnet test` 全緑。
- 伝票の論理パイプラインがインプロセスで動作し、実HTTPで集約済みペイロードを送信できる。
- 後続（第2計画）: 本計画のポート（`ISessionStore`/`IReadingStore`/`IProductCatalog`/`ISnapshotStore`/`ISecretStore`/`IDeviceFeedback`）を Azure SQL・Key Vault・C2D で実装し、IoT Hubトリガー関数（`Ingestion`）と完了/確定の関数（`Api`）から本サービス群を呼び出して実機E2Eへ。
