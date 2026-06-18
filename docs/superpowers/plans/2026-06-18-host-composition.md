# Azureアダプタ③a 支援アダプタ ＋ 合成ルート(DI) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Functions host が必要とする支援アダプタ（実時計・実ID・DNS解決・シークレットのTTLキャッシュ・Key Vault実装）と、これら＋既存SQL永続化＋Coreサービスを1つの **合成ルート `AddEpcForwarder(...)`** に束ねる。インプロセスで検証できる部分（キャッシュ/解決/時計/ID、そして「実オブジェクトグラフがDIで組み上がる」こと）を単体テストで固める。

**Architecture:** `EpcForwarder.Infrastructure` に各アダプタを実装し、`AddEpcForwarder(IServiceCollection, EpcForwarderOptions)` で `AddSqlPersistence` ＋ アダプタ ＋ Coreサービス（`ReadingIngestor`/`ShipmentReconciler`/`ShipmentDeliverer`/`InventoryDeliverer`/`SnapshotPublisher`/`ProductRegistrar`/`PayloadBuilder`）＋ `HttpWebhookSender` を登録する。`ISecretStore` は「Key Vault（または未設定時はNull）実装」を `CachingSecretStore` で包む二層構成。

**Tech Stack:** C# / .NET 8、Microsoft.Extensions.DependencyInjection(.Abstractions)、Azure.Security.KeyVault.Secrets + Azure.Identity、xUnit。

**Scope boundary:**
- 含む: `SystemClock`/`GuidIdGenerator`/`DnsHostResolver`、`CachingSecretStore`(TTL)＋`NullSecretStore`、`KeyVaultSecretStore`(build-only)、`NullDeviceFeedback`(プレースホルダ)、`EpcForwarderOptions`＋`AddEpcForwarder` 合成ルート、DI解決テスト。
- 含まない（③bおよび後続）: Functions本体（IoT Hubトリガー `Ingestion`/HTTP API `Api`）、Bicep、実機デプロイ。**実C2D配信**（`IDeviceFeedback` は C2D 送信先デバイスIDを必要とするがポートが運んでいない＝設計ギャップ。後述）。
- **検証範囲（合意済み）**: ローカル単体＋DI解決まで。Key Vault実装はビルド通過のみ（実検証は実機）。

**Prerequisites:** `dotnet` は ~/.dotnet。各コマンド前に `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH`。全テスト: `dotnet test EpcForwarder.sln --nologo`（Docker稼働下で既存のSQL統合テストも走る）。`Directory.Build.props` は `TreatWarningsAsErrors=true`。

**設計メモ:**
- `IDeviceFeedback.SendReachabilityAsync(Guid sessionId, ReachabilityResult, ct)` は **C2D の宛先デバイスIDを持たない**。実IoT Hub C2D実装はデバイス特定の設計（ポート拡張 or セッション→デバイス解決）が要るため③a範囲外とし、`NullDeviceFeedback`（ログのみ）を登録する。実装は③b以降で扱う。
- `IHostResolver`/`IWebhookSender` は登録するが、URLガード(`WebhookUrlGuard`)の配信経路への組み込みは③b（host側）で行う。
- 単体テスト/アダプタ実装の配置は `EpcForwarder.Infrastructure.Tests`（既存）。Testcontainers コレクション外なのでコンテナは起動しない。

---

## File Structure

| ファイル | 責務 |
|---|---|
| `src/EpcForwarder.Infrastructure/Runtime/SystemClock.cs` | `IClock` 実装（UTC現在） |
| `src/EpcForwarder.Infrastructure/Runtime/GuidIdGenerator.cs` | `IIdGenerator` 実装 |
| `src/EpcForwarder.Infrastructure/Net/DnsHostResolver.cs` | `IHostResolver` 実装（System.Net.Dns） |
| `src/EpcForwarder.Infrastructure/Secrets/NullSecretStore.cs` | 常に null（Key Vault未設定時の内側） |
| `src/EpcForwarder.Infrastructure/Secrets/CachingSecretStore.cs` | `ISecretStore` TTLキャッシュ・デコレータ |
| `src/EpcForwarder.Infrastructure/Secrets/KeyVaultSecretStore.cs` | Key Vault実装（build-only） |
| `src/EpcForwarder.Infrastructure/Messaging/NullDeviceFeedback.cs` | `IDeviceFeedback` no-op（プレースホルダ） |
| `src/EpcForwarder.Infrastructure/DependencyInjection/EpcForwarderOptions.cs` | 構成オプション |
| `src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` | `AddEpcForwarder(...)` 合成ルート |
| `src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj`（変更） | Azure Key Vault / Identity / Logging.Abstractions パッケージ |
| `tests/EpcForwarder.Infrastructure.Tests/`（追加） | アダプタ単体テスト＋DI解決テスト（+ Microsoft.Extensions.DependencyInjection 参照） |

---

## Task 1: SystemClock ＋ GuidIdGenerator ＋ DnsHostResolver

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Runtime/SystemClock.cs`, `src/EpcForwarder.Infrastructure/Runtime/GuidIdGenerator.cs`, `src/EpcForwarder.Infrastructure/Net/DnsHostResolver.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/Adapters/RuntimeAdapterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/Adapters/RuntimeAdapterTests.cs
using System.Net;
using EpcForwarder.Infrastructure.Net;
using EpcForwarder.Infrastructure.Runtime;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests.Adapters;

public class RuntimeAdapterTests
{
    [Fact]
    public void SystemClock_ReturnsUtcNow_WithinTolerance()
    {
        var before = DateTimeOffset.UtcNow;
        var now = new SystemClock().UtcNow;
        var after = DateTimeOffset.UtcNow;
        Assert.InRange(now, before, after);
    }

    [Fact]
    public void GuidIdGenerator_ProducesDistinctNonEmptyGuids()
    {
        var g = new GuidIdGenerator();
        var a = g.NewGuid();
        var b = g.NewGuid();
        Assert.NotEqual(Guid.Empty, a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DnsHostResolver_ResolvesIpLiteral_ToSameAddress()
    {
        var ips = new DnsHostResolver().Resolve("127.0.0.1");
        Assert.Contains(IPAddress.Parse("127.0.0.1"), ips);
    }

    [Fact]
    public void DnsHostResolver_ResolvesLocalhost_ToLoopback()
    {
        var ips = new DnsHostResolver().Resolve("localhost");
        Assert.Contains(ips, IPAddress.IsLoopback);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~RuntimeAdapterTests`
Expected: コンパイル失敗（型未定義）。

- [ ] **Step 3: Implement**

```csharp
// src/EpcForwarder.Infrastructure/Runtime/SystemClock.cs
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Runtime;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

```csharp
// src/EpcForwarder.Infrastructure/Runtime/GuidIdGenerator.cs
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Runtime;

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewGuid() => Guid.NewGuid();
}
```

```csharp
// src/EpcForwarder.Infrastructure/Net/DnsHostResolver.cs
using System.Net;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Net;

/// <summary>System.Net.Dns によるホスト解決。IPリテラルはそのまま返る。</summary>
public sealed class DnsHostResolver : IHostResolver
{
    public IReadOnlyList<IPAddress> Resolve(string host) => Dns.GetHostAddresses(host);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~RuntimeAdapterTests`
Expected: PASS（4ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Runtime/ src/EpcForwarder.Infrastructure/Net/ tests/EpcForwarder.Infrastructure.Tests/Adapters/RuntimeAdapterTests.cs
git commit -m "feat(infra): SystemClock / GuidIdGenerator / DnsHostResolver"
```

---

## Task 2: CachingSecretStore（TTL）＋ NullSecretStore

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Secrets/NullSecretStore.cs`, `src/EpcForwarder.Infrastructure/Secrets/CachingSecretStore.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/Adapters/CachingSecretStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/Adapters/CachingSecretStoreTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Infrastructure.Secrets;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests.Adapters;

public class CachingSecretStoreTests
{
    private sealed class CountingInner : ISecretStore
    {
        public int Calls { get; private set; }
        public string? Value { get; set; } = "v1";
        public Task<string?> GetAsync(string name, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Value);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public async Task GetAsync_CachesWithinTtl_InnerCalledOnce()
    {
        var inner = new CountingInner();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sut = new CachingSecretStore(inner, clock, TimeSpan.FromMinutes(5));

        var a = await sut.GetAsync("s");
        var b = await sut.GetAsync("s");

        Assert.Equal("v1", a);
        Assert.Equal("v1", b);
        Assert.Equal(1, inner.Calls); // 2回目はキャッシュ
    }

    [Fact]
    public async Task GetAsync_RefetchesAfterTtlExpiry()
    {
        var inner = new CountingInner();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sut = new CachingSecretStore(inner, clock, TimeSpan.FromMinutes(5));

        await sut.GetAsync("s");
        clock.UtcNow = clock.UtcNow.AddMinutes(6); // TTL超過
        inner.Value = "v2";
        var after = await sut.GetAsync("s");

        Assert.Equal("v2", after);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task GetAsync_DoesNotCacheNull()
    {
        var inner = new CountingInner { Value = null };
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var sut = new CachingSecretStore(inner, clock, TimeSpan.FromMinutes(5));

        await sut.GetAsync("s");
        await sut.GetAsync("s");

        Assert.Equal(2, inner.Calls); // null は毎回再取得
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~CachingSecretStoreTests`
Expected: コンパイル失敗（型未定義）。

- [ ] **Step 3: Implement**

```csharp
// src/EpcForwarder.Infrastructure/Secrets/NullSecretStore.cs
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Secrets;

/// <summary>シークレットを持たない環境用(常に null)。Key Vault 未設定時の内側に使う。</summary>
public sealed class NullSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult<string?>(null);
}
```

```csharp
// src/EpcForwarder.Infrastructure/Secrets/CachingSecretStore.cs
using System.Collections.Concurrent;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Secrets;

/// <summary>内側の ISecretStore を TTL でキャッシュする。非nullのみキャッシュ(nullは毎回再取得)。</summary>
public sealed class CachingSecretStore(ISecretStore inner, IClock clock, TimeSpan ttl) : ISecretStore
{
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> _cache = new();

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(name, out var entry) && clock.UtcNow < entry.ExpiresAt)
        {
            return entry.Value;
        }

        var value = await inner.GetAsync(name, ct);
        if (value is not null)
        {
            _cache[name] = (value, clock.UtcNow.Add(ttl));
        }

        return value;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~CachingSecretStoreTests`
Expected: PASS（3ケース）。

- [ ] **Step 5: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Secrets/ tests/EpcForwarder.Infrastructure.Tests/Adapters/CachingSecretStoreTests.cs
git commit -m "feat(infra): CachingSecretStore(TTL) と NullSecretStore"
```

---

## Task 3: KeyVaultSecretStore（build-only）＋ NullDeviceFeedback

実Azure必須のためビルド通過のみ。単体テストは付けない（実検証は実機/④手順書）。

**Files:**
- Modify: `src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj`（パッケージ追加）
- Create: `src/EpcForwarder.Infrastructure/Secrets/KeyVaultSecretStore.cs`, `src/EpcForwarder.Infrastructure/Messaging/NullDeviceFeedback.cs`

- [ ] **Step 1: Add packages**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add src/EpcForwarder.Infrastructure package Azure.Security.KeyVault.Secrets
dotnet add src/EpcForwarder.Infrastructure package Azure.Identity
dotnet add src/EpcForwarder.Infrastructure package Microsoft.Extensions.Logging.Abstractions
```

- [ ] **Step 2: Implement (build-only)**

```csharp
// src/EpcForwarder.Infrastructure/Secrets/KeyVaultSecretStore.cs
using Azure;
using Azure.Security.KeyVault.Secrets;
using EpcForwarder.Core.Abstractions;

namespace EpcForwarder.Infrastructure.Secrets;

/// <summary>Azure Key Vault からシークレットを取得。実検証は実機(本クラスは build-only)。</summary>
public sealed class KeyVaultSecretStore(SecretClient client) : ISecretStore
{
    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetSecretAsync(name, cancellationToken: ct);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
```

```csharp
// src/EpcForwarder.Infrastructure/Messaging/NullDeviceFeedback.cs
using EpcForwarder.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EpcForwarder.Infrastructure.Messaging;

/// <summary>
/// 端末フィードバックのプレースホルダ。実C2D配信は宛先デバイスIDの特定設計が必要なため未実装(③b以降)。
/// 当面はログ出力のみ。
/// </summary>
public sealed class NullDeviceFeedback(ILogger<NullDeviceFeedback> logger) : IDeviceFeedback
{
    public Task SendReachabilityAsync(Guid sessionId, ReachabilityResult result, CancellationToken ct = default)
    {
        logger.LogInformation(
            "Reachability feedback (no-op): session={SessionId} expected={Expected} received={Received}",
            sessionId, result.Expected, result.Received);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build EpcForwarder.sln --nologo -v q`
Expected: 0 警告 / 0 エラー。

- [ ] **Step 4: Commit**

```bash
git add src/EpcForwarder.Infrastructure/Secrets/KeyVaultSecretStore.cs src/EpcForwarder.Infrastructure/Messaging/NullDeviceFeedback.cs src/EpcForwarder.Infrastructure/EpcForwarder.Infrastructure.csproj
git commit -m "feat(infra): KeyVaultSecretStore(build-only) と NullDeviceFeedback(placeholder)"
```

---

## Task 4: EpcForwarderOptions ＋ AddEpcForwarder 合成ルート

**Files:**
- Create: `src/EpcForwarder.Infrastructure/DependencyInjection/EpcForwarderOptions.cs`, `src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Implement options and composition root**

```csharp
// src/EpcForwarder.Infrastructure/DependencyInjection/EpcForwarderOptions.cs
namespace EpcForwarder.Infrastructure.DependencyInjection;

public sealed class EpcForwarderOptions
{
    public required string SqlConnectionString { get; init; }
    /// <summary>未設定なら Key Vault を使わず NullSecretStore を内側にする。</summary>
    public string? KeyVaultUri { get; init; }
    public TimeSpan SecretCacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
```

```csharp
// src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.Delivery;
using EpcForwarder.Infrastructure.Messaging;
using EpcForwarder.Infrastructure.Net;
using EpcForwarder.Infrastructure.Persistence;
using EpcForwarder.Infrastructure.Runtime;
using EpcForwarder.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace EpcForwarder.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>SQL永続化＋アダプタ＋Coreサービスを1つのオブジェクトグラフとして登録する。</summary>
    public static IServiceCollection AddEpcForwarder(this IServiceCollection services, EpcForwarderOptions options)
    {
        // 永続化(4ポート)
        services.AddSqlPersistence(options.SqlConnectionString);

        // ランタイム・アダプタ
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<IDeviceFeedback, NullDeviceFeedback>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();

        // シークレット: (Key Vault or Null) を TTL キャッシュで包む
        services.AddSingleton<ISecretStore>(sp =>
        {
            ISecretStore inner = options.KeyVaultUri is { Length: > 0 } uri
                ? new KeyVaultSecretStore(new SecretClient(new Uri(uri), new DefaultAzureCredential()))
                : new NullSecretStore();
            return new CachingSecretStore(inner, sp.GetRequiredService<IClock>(), options.SecretCacheTtl);
        });

        // Core サービス
        services.AddSingleton<PayloadBuilder>();
        services.AddSingleton<SnapshotPublisher>();
        services.AddSingleton<ReadingIngestor>();
        services.AddSingleton<ShipmentReconciler>();
        services.AddSingleton<ShipmentDeliverer>();
        services.AddSingleton<InventoryDeliverer>();
        services.AddSingleton<ProductRegistrar>();

        return services;
    }
}
```
> `NullDeviceFeedback` は `ILogger<NullDeviceFeedback>` を要求するため、利用側(テスト/ホスト)は `services.AddLogging()` を呼ぶ。`SqlConnectionFactory` は登録時に接続しない（解決のみで graph が組める）。`HttpClient` は当面シングルトン（本番は `IHttpClientFactory` 推奨＝③bで検討）。

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build EpcForwarder.sln --nologo -v q`
Expected: 0 警告 / 0 エラー。

- [ ] **Step 3: Commit**

```bash
git add src/EpcForwarder.Infrastructure/DependencyInjection/
git commit -m "feat(infra): AddEpcForwarder 合成ルートと EpcForwarderOptions"
```

---

## Task 5: DI解決テスト（オブジェクトグラフが組み上がる）

実Azure無し（ダミー接続文字列・Key Vault未設定）で、合成ルートから主要サービスが解決できることを検証する。`SqlConnectionFactory` は接続しないので DB 不要・Docker不要。

**Files:**
- Modify: `tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj`（`Microsoft.Extensions.DependencyInjection` 追加）
- Test: `tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs`

- [ ] **Step 1: Add the DI package**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add tests/EpcForwarder.Infrastructure.Tests package Microsoft.Extensions.DependencyInjection
dotnet add tests/EpcForwarder.Infrastructure.Tests package Microsoft.Extensions.Logging
```
（`Microsoft.Extensions.Logging` は `services.AddLogging()`（NullDeviceFeedback の `ILogger` 充足）に必要。）

- [ ] **Step 2: Write the test**

```csharp
// tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs
using EpcForwarder.Core.Abstractions;
using EpcForwarder.Core.Delivery;
using EpcForwarder.Core.Products;
using EpcForwarder.Core.Sessions;
using EpcForwarder.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests.Composition;

public class AddEpcForwarderTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(); // NullDeviceFeedback の ILogger 用
        services.AddEpcForwarder(new EpcForwarderOptions
        {
            SqlConnectionString = "Server=localhost;Database=epcf;Integrated Security=true;TrustServerCertificate=true",
            // KeyVaultUri 未設定 → NullSecretStore
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Theory]
    [InlineData(typeof(ReadingIngestor))]
    [InlineData(typeof(ShipmentReconciler))]
    [InlineData(typeof(ShipmentDeliverer))]
    [InlineData(typeof(InventoryDeliverer))]
    [InlineData(typeof(ProductRegistrar))]
    [InlineData(typeof(SnapshotPublisher))]
    [InlineData(typeof(IWebhookSender))]
    [InlineData(typeof(ISecretStore))]
    [InlineData(typeof(IClock))]
    [InlineData(typeof(IIdGenerator))]
    [InlineData(typeof(IHostResolver))]
    [InlineData(typeof(IDeviceFeedback))]
    public void Resolves_AllPrimaryServices(Type serviceType)
    {
        using var sp = Build();
        Assert.NotNull(sp.GetRequiredService(serviceType));
    }

    [Fact]
    public void SecretStore_IsCachingDecorator()
    {
        using var sp = Build();
        Assert.IsType<EpcForwarder.Infrastructure.Secrets.CachingSecretStore>(sp.GetRequiredService<ISecretStore>());
    }
}
```

- [ ] **Step 3: Run the test**

Run: `dotnet test tests/EpcForwarder.Infrastructure.Tests --nologo --filter FullyQualifiedName~AddEpcForwarderTests`
Expected: PASS（解決12ケース＋デコレータ確認）。Docker不要（DB接続しない）。

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test EpcForwarder.sln --nologo`
Expected: Core.Tests 全緑 ＋ Infrastructure.Tests 全緑（SQL統合テストは Docker稼働下）。

- [ ] **Step 5: Commit**

```bash
git add tests/EpcForwarder.Infrastructure.Tests/Composition/AddEpcForwarderTests.cs tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj
git commit -m "test(infra): AddEpcForwarder のDI解決テスト"
```

---

## 完了条件

- `dotnet build` 0警告/0エラー。`dotnet test EpcForwarder.sln` 全緑（Core＋Infrastructure、Docker稼働下でSQL統合も）。
- 支援アダプタ（時計/ID/DNS/TTLキャッシュ）が単体検証され、`AddEpcForwarder` が **実オブジェクトグラフ**（SQL永続化＋アダプタ＋Coreサービス＋実HTTP送信）をDIで組み上げられることを確認。Key Vault/C2D実体はビルド通過のみ。
- 後続: ③b Functions host（IoT Hubトリガー `Ingestion` ＋ HTTP API `Api`、`AddEpcForwarder` で結線、URLガード/`IDeviceFeedback`のデバイス特定設計）→ ④ Bicep ＋ 実機デプロイ手順書 → 実機E2E。
