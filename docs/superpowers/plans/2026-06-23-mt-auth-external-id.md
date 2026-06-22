# Entra External ID マルチテナント認証 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** HTTP API 5関数の認証を「Functionキー+自己申告ヘッダ」から「Entra External ID の JWT 検証 + トークン由来テナント」に置換する。

**Architecture:** Functions(.NET 8 isolated, ASP.NET Core 統合)に JWT 検証ミドルウェアを挿す。検証は純粋クラス `JwtBearerValidator`(テスト鍵で単体テスト可能)。トークンの tenant クレーム(`tenant.code`)を `ITenantLookup` で内部 `tenant_id` に解決し `FunctionContext` に載せる。関数は `Anonymous` 化し、ヘッダ参照(`RequestTenant`)を `AuthenticatedTenant` に置換。取込(IoT)経路は対象外。

**Tech Stack:** C#/.NET 8 isolated worker, Microsoft.IdentityModel.JsonWebTokens / Protocols.OpenIdConnect(JWT/JWKS), Dapper + Microsoft.Data.SqlClient, xUnit + Testcontainers.MsSql。

**設計根拠:** `docs/superpowers/specs/2026-06-23-mt-auth-external-id-design.md`

---

## File Structure

| ファイル | 責務 |
|---|---|
| `src/EpcForwarder.Core/Abstractions/Ports.cs`(追記) | `ITenantLookup { int? ResolveId(string code); }` |
| `src/EpcForwarder.Infrastructure/Persistence/SqlTenantLookup.cs`(新規) | `tenant.code → tenant_id` 解決(メモ化キャッシュ付) |
| `src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`(変更) | `ITenantLookup` を DI 登録 |
| `src/EpcForwarder.Functions/Auth/AuthOptions.cs`(新規) | `Issuer`/`Audience`/`TenantClaim`/`MetadataAddress` 設定 |
| `src/EpcForwarder.Functions/Auth/TokenValidationResult.cs`(新規) | 検証結果(Authenticated(code)/NoTenant/Unauthenticated)の判別型 |
| `src/EpcForwarder.Functions/Auth/ITokenValidationParametersProvider.cs`(新規) | `TokenValidationParameters` 供給の抽象(テスト差し替え用) |
| `src/EpcForwarder.Functions/Auth/JwtBearerValidator.cs`(新規) | トークン検証 + tenant クレーム抽出(純ロジック) |
| `src/EpcForwarder.Functions/Auth/OpenIdTokenValidationParametersProvider.cs`(新規) | OIDC メタデータ/JWKS から検証パラメータを構築(本番用) |
| `src/EpcForwarder.Functions/Auth/AuthenticatedTenant.cs`(新規) | `FunctionContext` から認証済み tenant_id を出し入れ |
| `src/EpcForwarder.Functions/Auth/AuthenticationMiddleware.cs`(新規) | HTTP 関数にトークン検証→tenant解決→格納。失敗時 401/403 |
| `src/EpcForwarder.Functions/Program.cs`(変更) | ミドルウェア登録 + AuthOptions/Validator DI |
| `src/EpcForwarder.Functions/Api/SessionQueryApi.cs`(変更) | `Anonymous` 化 + `AuthenticatedTenant` 利用 |
| `src/EpcForwarder.Functions/Api/InventoryApi.cs`(変更) | 同上 |
| `src/EpcForwarder.Functions/Api/RequestTenant.cs`(削除) | `AuthenticatedTenant` に置換 |
| `tests/EpcForwarder.Infrastructure.Tests/SqlTenantLookupTests.cs`(新規) | code→id 解決の検証 |
| `tests/EpcForwarder.Functions.Tests/JwtBearerValidatorTests.cs`(新規) | テスト鍵での検証全ケース |
| `docs/runbooks/entra-external-id.md`(新規) | External ID 構築手順 |

---

## Task 1: `ITenantLookup` + `SqlTenantLookup`(TDD)

tenant.code → tenant_id の解決。Core にポート、Infrastructure に実装。

**Files:**
- Modify: `src/EpcForwarder.Core/Abstractions/Ports.cs`
- Create: `src/EpcForwarder.Infrastructure/Persistence/SqlTenantLookup.cs`
- Modify: `src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SqlTenantLookupTests.cs`

- [ ] **Step 1: ポートを追加**

`src/EpcForwarder.Core/Abstractions/Ports.cs` の `ISessionStore` 定義(16-20行目付近)の直後に追記:

```csharp
/// <summary>テナント識別コード(tenant.code)を内部 tenant_id に解決する。未知なら null。</summary>
public interface ITenantLookup
{
    int? ResolveId(string code);
}
```

- [ ] **Step 2: 失敗するテストを書く**

`tests/EpcForwarder.Infrastructure.Tests/SqlTenantLookupTests.cs` を新規作成:

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SqlTenantLookupTests.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SqlTenantLookupTests(SqlServerFixture fx)
{
    [Fact]
    public void ResolveId_ReturnsTenantId_ForKnownCode()
    {
        var code = $"acme-{Guid.NewGuid():N}";
        int expected;
        using (var conn = new SqlConnection(fx.ConnectionString))
        {
            expected = conn.QuerySingle<int>(
                "INSERT INTO dbo.tenant(code,name) OUTPUT INSERTED.tenant_id VALUES(@c,'Acme')",
                new { c = code });
        }

        var sut = new SqlTenantLookup(fx.ConnectionString);

        Assert.Equal(expected, sut.ResolveId(code));
    }

    [Fact]
    public void ResolveId_ReturnsNull_ForUnknownCode()
    {
        var sut = new SqlTenantLookup(fx.ConnectionString);
        Assert.Null(sut.ResolveId($"missing-{Guid.NewGuid():N}"));
    }
}
```

- [ ] **Step 3: ビルド失敗を確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj -c Release 2>&1 | tail -5
```
Expected: `SqlTenantLookup` 未定義のコンパイルエラー(CS0246)。

- [ ] **Step 4: `SqlTenantLookup` を実装**

`src/EpcForwarder.Infrastructure/Persistence/SqlTenantLookup.cs` を新規作成。tenant.code は UNIQUE。解決済みの code→id はメモ化(id は不変なので無効化不要):

```csharp
// src/EpcForwarder.Infrastructure/Persistence/SqlTenantLookup.cs
using System.Collections.Concurrent;
using Dapper;
using EpcForwarder.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

/// <summary>tenant.code → tenant_id を解決。code→id は不変なのでプロセス内メモ化する。</summary>
public sealed class SqlTenantLookup(string connectionString) : ITenantLookup
{
    private readonly ConcurrentDictionary<string, int> _cache = new(StringComparer.Ordinal);

    public int? ResolveId(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        if (_cache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        using var conn = new SqlConnection(connectionString);
        var id = conn.QuerySingleOrDefault<int?>(
            "SELECT tenant_id FROM dbo.tenant WHERE code = @code",
            new { code });

        if (id is int value)
        {
            _cache[code] = value;
        }

        return id;
    }
}
```

- [ ] **Step 5: DI 登録**

`src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` を開き、既存の SQL ストア登録(`SqlSessionStore` 等を登録している箇所)に倣って `ITenantLookup` を追加する。具体的には、`AddEpcForwarder` 内で接続文字列を使って各 `Sql*Store` を登録しているブロックに次の1行を加える:

```csharp
services.AddSingleton<EpcForwarder.Core.Abstractions.ITenantLookup>(
    _ => new EpcForwarder.Infrastructure.Persistence.SqlTenantLookup(options.SqlConnectionString));
```

> 既存登録の正確な書式(ラムダ/接続文字列の取り回し)はファイル内の `SqlSessionStore` 登録行に合わせること。`options.SqlConnectionString` は `AddEpcForwarder(EpcForwarderOptions options)` の引数。

- [ ] **Step 6: テスト緑を確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet test tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~SqlTenantLookupTests" 2>&1 | grep -iE "合格|失敗|passed|failed" | tail -3
```
Expected: `合格: 2`(失敗 0)。

- [ ] **Step 7: コミット**

```bash
git add src/EpcForwarder.Core/Abstractions/Ports.cs src/EpcForwarder.Infrastructure/Persistence/SqlTenantLookup.cs src/EpcForwarder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs tests/EpcForwarder.Infrastructure.Tests/SqlTenantLookupTests.cs
git commit -m "feat(infra): ITenantLookup + SqlTenantLookup(code→tenant_id 解決)"
```

---

## Task 2: `AuthOptions` + `JwtBearerValidator`(TDD・純ロジック)

トークン検証と tenant クレーム抽出。検証パラメータは抽象経由で注入し、テストでは test 鍵を差し込む。

**Files:**
- Create: `src/EpcForwarder.Functions/Auth/AuthOptions.cs`
- Create: `src/EpcForwarder.Functions/Auth/TokenValidationResult.cs`
- Create: `src/EpcForwarder.Functions/Auth/ITokenValidationParametersProvider.cs`
- Create: `src/EpcForwarder.Functions/Auth/JwtBearerValidator.cs`
- Modify: `src/EpcForwarder.Functions/EpcForwarder.Functions.csproj`(パッケージ追加)
- Test: `tests/EpcForwarder.Functions.Tests/JwtBearerValidatorTests.cs`

- [ ] **Step 1: NuGet パッケージを追加**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet add src/EpcForwarder.Functions/EpcForwarder.Functions.csproj package Microsoft.IdentityModel.JsonWebTokens
dotnet add src/EpcForwarder.Functions/EpcForwarder.Functions.csproj package Microsoft.IdentityModel.Protocols.OpenIdConnect
```
Expected: 2パッケージ追加。解決されたバージョンを csproj に固定。ビルド警告ゼロ(`TreatWarningsAsErrors=true`)を維持。

- [ ] **Step 2: 設定・結果・プロバイダ抽象を作る**

`src/EpcForwarder.Functions/Auth/AuthOptions.cs`:
```csharp
// src/EpcForwarder.Functions/Auth/AuthOptions.cs
namespace EpcForwarder.Functions.Auth;

/// <summary>External ID トークン検証の設定(アプリ設定 Auth__* から束縛)。</summary>
public sealed class AuthOptions
{
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    /// <summary>テナントを運ぶクレーム名。External ID では extension_&lt;appid&gt;_tenantId 形式。</summary>
    public string TenantClaim { get; init; } = "";
    /// <summary>OIDC メタデータURL(.../v2.0/.well-known/openid-configuration)。</summary>
    public string MetadataAddress { get; init; } = "";
}
```

`src/EpcForwarder.Functions/Auth/TokenValidationResult.cs`:
```csharp
// src/EpcForwarder.Functions/Auth/TokenValidationResult.cs
namespace EpcForwarder.Functions.Auth;

public enum AuthOutcome { Authenticated, NoTenant, Unauthenticated }

/// <summary>トークン検証の結果。Authenticated のときのみ TenantCode を持つ。</summary>
public sealed record TokenValidationResult(AuthOutcome Outcome, string? TenantCode)
{
    public static readonly TokenValidationResult Unauthenticated = new(AuthOutcome.Unauthenticated, null);
    public static readonly TokenValidationResult NoTenant = new(AuthOutcome.NoTenant, null);
    public static TokenValidationResult Authenticated(string tenantCode) => new(AuthOutcome.Authenticated, tenantCode);
}
```

`src/EpcForwarder.Functions/Auth/ITokenValidationParametersProvider.cs`:
```csharp
// src/EpcForwarder.Functions/Auth/ITokenValidationParametersProvider.cs
using Microsoft.IdentityModel.Tokens;

namespace EpcForwarder.Functions.Auth;

/// <summary>署名鍵を含む検証パラメータを供給する。本番は OIDC メタデータ由来、テストは固定鍵。</summary>
public interface ITokenValidationParametersProvider
{
    Task<TokenValidationParameters> GetAsync(CancellationToken ct);
}
```

- [ ] **Step 3: 失敗するテストを書く**

`tests/EpcForwarder.Functions.Tests/JwtBearerValidatorTests.cs` を新規作成。test RSA 鍵で署名したトークンを作り、同じ鍵を検証パラメータに渡す:

```csharp
// tests/EpcForwarder.Functions.Tests/JwtBearerValidatorTests.cs
using System.Security.Claims;
using System.Security.Cryptography;
using EpcForwarder.Functions.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EpcForwarder.Functions.Tests;

public sealed class JwtBearerValidatorTests
{
    private const string Issuer = "https://test.ciamlogin.com/";
    private const string Audience = "api://epcf-test";
    private const string TenantClaim = "extension_tenantId";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };

    private sealed class FixedProvider(SecurityKey key) : ITokenValidationParametersProvider
    {
        public Task<TokenValidationParameters> GetAsync(CancellationToken ct) =>
            Task.FromResult(new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                IssuerSigningKey = key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            });
    }

    private JwtBearerValidator NewSut() =>
        new(new FixedProvider(_key), new AuthOptions { Issuer = Issuer, Audience = Audience, TenantClaim = TenantClaim });

    private string MakeToken(
        string? tenant = "acme",
        string? issuer = Issuer,
        string? audience = Audience,
        DateTime? expires = null,
        SecurityKey? signingKey = null)
    {
        var claims = new List<Claim>();
        if (tenant is not null) claims.Add(new Claim(TenantClaim, tenant));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(signingKey ?? _key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task Valid_token_returns_authenticated_with_tenant()
    {
        var r = await NewSut().ValidateAsync(MakeToken(tenant: "acme"), default);
        Assert.Equal(AuthOutcome.Authenticated, r.Outcome);
        Assert.Equal("acme", r.TenantCode);
    }

    [Fact]
    public async Task Missing_tenant_claim_returns_no_tenant()
    {
        var r = await NewSut().ValidateAsync(MakeToken(tenant: null), default);
        Assert.Equal(AuthOutcome.NoTenant, r.Outcome);
    }

    [Fact]
    public async Task Wrong_issuer_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync(MakeToken(issuer: "https://evil/"), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Wrong_audience_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync(MakeToken(audience: "api://other"), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Expired_token_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync(MakeToken(expires: DateTime.UtcNow.AddMinutes(-5)), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Wrong_signing_key_is_unauthenticated()
    {
        var other = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "other" };
        var r = await NewSut().ValidateAsync(MakeToken(signingKey: other), default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }

    [Fact]
    public async Task Garbage_token_is_unauthenticated()
    {
        var r = await NewSut().ValidateAsync("not-a-jwt", default);
        Assert.Equal(AuthOutcome.Unauthenticated, r.Outcome);
    }
}
```

- [ ] **Step 4: ビルド失敗を確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj -c Release 2>&1 | tail -5
```
Expected: `JwtBearerValidator` 未定義のコンパイルエラー。

- [ ] **Step 5: `JwtBearerValidator` を実装**

`src/EpcForwarder.Functions/Auth/JwtBearerValidator.cs`:
```csharp
// src/EpcForwarder.Functions/Auth/JwtBearerValidator.cs
using Microsoft.IdentityModel.JsonWebTokens;

namespace EpcForwarder.Functions.Auth;

/// <summary>External ID の JWT を検証し tenant クレームを取り出す。署名鍵はプロバイダ経由。</summary>
public sealed class JwtBearerValidator(ITokenValidationParametersProvider parametersProvider, AuthOptions options)
{
    private readonly JsonWebTokenHandler _handler = new();

    public async Task<TokenValidationResult> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenValidationResult.Unauthenticated;
        }

        var parameters = await parametersProvider.GetAsync(ct);
        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            return TokenValidationResult.Unauthenticated;
        }

        var tenant = result.ClaimsIdentity.FindFirst(options.TenantClaim)?.Value;
        return string.IsNullOrEmpty(tenant)
            ? TokenValidationResult.NoTenant
            : TokenValidationResult.Authenticated(tenant);
    }
}
```

- [ ] **Step 6: テスト緑を確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet test tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj -c Release --filter "FullyQualifiedName~JwtBearerValidatorTests" 2>&1 | grep -iE "合格|失敗|passed|failed" | tail -3
```
Expected: `合格: 7`(失敗 0)。

- [ ] **Step 7: コミット**

```bash
git add src/EpcForwarder.Functions/Auth/ src/EpcForwarder.Functions/EpcForwarder.Functions.csproj tests/EpcForwarder.Functions.Tests/JwtBearerValidatorTests.cs
git commit -m "feat(functions): JwtBearerValidator(External ID トークン検証+tenantクレーム抽出)"
```

---

## Task 3: 本番用 `OpenIdTokenValidationParametersProvider`

OIDC メタデータ/JWKS から検証パラメータを構築(ネットワーク依存のため単体テストなし。ビルド検証のみ)。

**Files:**
- Create: `src/EpcForwarder.Functions/Auth/OpenIdTokenValidationParametersProvider.cs`

- [ ] **Step 1: 実装**

`src/EpcForwarder.Functions/Auth/OpenIdTokenValidationParametersProvider.cs`。`ConfigurationManager` が JWKS をキャッシュ・自動更新する:

```csharp
// src/EpcForwarder.Functions/Auth/OpenIdTokenValidationParametersProvider.cs
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EpcForwarder.Functions.Auth;

/// <summary>OIDC メタデータから署名鍵(JWKS)を取得して検証パラメータを構築。鍵はキャッシュ・自動更新。</summary>
public sealed class OpenIdTokenValidationParametersProvider : ITokenValidationParametersProvider
{
    private readonly AuthOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager;

    public OpenIdTokenValidationParametersProvider(AuthOptions options)
    {
        _options = options;
        _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            options.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever());
    }

    public async Task<TokenValidationParameters> GetAsync(CancellationToken ct)
    {
        var config = await _configManager.GetConfigurationAsync(ct);
        return new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
        };
    }
}
```

- [ ] **Step 2: ビルド確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build src/EpcForwarder.Functions/EpcForwarder.Functions.csproj -c Release 2>&1 | tail -4
```
Expected: `ビルドに成功しました` / `0 エラー` / `0 警告`。

- [ ] **Step 3: コミット**

```bash
git add src/EpcForwarder.Functions/Auth/OpenIdTokenValidationParametersProvider.cs
git commit -m "feat(functions): OIDC メタデータ由来の検証パラメータプロバイダ(本番用)"
```

---

## Task 4: `AuthenticatedTenant` + `AuthenticationMiddleware` + Program.cs 結線

ミドルウェアで HTTP 関数のトークンを検証し tenant_id を文脈に載せる。

**Files:**
- Create: `src/EpcForwarder.Functions/Auth/AuthenticatedTenant.cs`
- Create: `src/EpcForwarder.Functions/Auth/AuthenticationMiddleware.cs`
- Modify: `src/EpcForwarder.Functions/Program.cs`

- [ ] **Step 1: `AuthenticatedTenant` を実装**

`src/EpcForwarder.Functions/Auth/AuthenticatedTenant.cs`。`FunctionContext.Items` をストレージに使う:

```csharp
// src/EpcForwarder.Functions/Auth/AuthenticatedTenant.cs
using Microsoft.Azure.Functions.Worker;

namespace EpcForwarder.Functions.Auth;

/// <summary>ミドルウェアが解決した認証済み tenant_id を関数へ受け渡す。</summary>
public static class AuthenticatedTenant
{
    private const string Key = "EpcfTenantId";

    public static void Set(FunctionContext context, int tenantId) => context.Items[Key] = tenantId;

    public static bool TryGet(FunctionContext context, out int tenantId)
    {
        if (context.Items.TryGetValue(Key, out var v) && v is int id)
        {
            tenantId = id;
            return true;
        }
        tenantId = 0;
        return false;
    }
}
```

- [ ] **Step 2: `AuthenticationMiddleware` を実装**

`src/EpcForwarder.Functions/Auth/AuthenticationMiddleware.cs`。HTTP 関数のみ対象(`GetHttpContext()` が null の取込関数はスキップ)。失敗時は 401/403 を書いて関数本体を呼ばない:

```csharp
// src/EpcForwarder.Functions/Auth/AuthenticationMiddleware.cs
using EpcForwarder.Core.Abstractions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.AspNetCore.Http;

namespace EpcForwarder.Functions.Auth;

/// <summary>HTTP 関数の External ID トークンを検証し tenant_id を文脈へ。失敗は 401/403 で短絡。</summary>
public sealed class AuthenticationMiddleware(JwtBearerValidator validator, ITenantLookup tenants) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var http = context.GetHttpContext();
        if (http is null)
        {
            // 非HTTP(取込 EventHub トリガー等)は対象外。
            await next(context);
            return;
        }

        string? token = null;
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = header["Bearer ".Length..].Trim();
        }

        var result = await validator.ValidateAsync(token ?? "", context.CancellationToken);
        if (result.Outcome == AuthOutcome.Unauthenticated)
        {
            await WriteStatus(http, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }

        if (result.Outcome == AuthOutcome.NoTenant || tenants.ResolveId(result.TenantCode!) is not int tenantId)
        {
            await WriteStatus(http, StatusCodes.Status403Forbidden, null);
            return;
        }

        AuthenticatedTenant.Set(context, tenantId);
        await next(context);
    }

    private static async Task WriteStatus(HttpContext http, int code, string? error)
    {
        http.Response.StatusCode = code;
        if (code == StatusCodes.Status401Unauthorized)
        {
            http.Response.Headers.WWWAuthenticate = error is null ? "Bearer" : $"Bearer error=\"{error}\"";
        }
        await http.Response.WriteAsync(code == 401 ? "Unauthorized" : "Forbidden");
    }
}
```

- [ ] **Step 3: Program.cs に結線**

`src/EpcForwarder.Functions/Program.cs` を次の内容に更新(AuthOptions 束縛・Validator/Provider DI・ミドルウェア登録を追加):

```csharp
using EpcForwarder.Functions.Auth;
using EpcForwarder.Infrastructure.DependencyInjection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights(.NET isolated 標準連携)。
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

var sqlConnectionString = builder.Configuration["SqlConnectionString"]
    ?? throw new InvalidOperationException("App setting 'SqlConnectionString' is required.");

builder.Services.AddEpcForwarder(new EpcForwarderOptions
{
    SqlConnectionString = sqlConnectionString,
    KeyVaultUri = builder.Configuration["KeyVaultUri"],
});

// External ID トークン検証(HTTP API 用)。
var authOptions = new AuthOptions
{
    Issuer = builder.Configuration["Auth:Issuer"] ?? "",
    Audience = builder.Configuration["Auth:Audience"] ?? "",
    TenantClaim = builder.Configuration["Auth:TenantClaim"] ?? "",
    MetadataAddress = builder.Configuration["Auth:MetadataAddress"] ?? "",
};
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton<ITokenValidationParametersProvider>(_ => new OpenIdTokenValidationParametersProvider(authOptions));
builder.Services.AddSingleton<JwtBearerValidator>();

builder.UseMiddleware<AuthenticationMiddleware>();

builder.Build().Run();
```

> 注: アプリ設定キーは環境変数では `Auth__Issuer` 等の二重アンダースコア。構成バインドでは `Auth:Issuer` で読む(同一設定)。

- [ ] **Step 4: ビルド確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build src/EpcForwarder.Functions/EpcForwarder.Functions.csproj -c Release 2>&1 | tail -4
```
Expected: `ビルドに成功しました` / `0 エラー` / `0 警告`。

- [ ] **Step 5: コミット**

```bash
git add src/EpcForwarder.Functions/Auth/AuthenticatedTenant.cs src/EpcForwarder.Functions/Auth/AuthenticationMiddleware.cs src/EpcForwarder.Functions/Program.cs
git commit -m "feat(functions): 認証ミドルウェア結線(JWT検証→tenant解決→文脈格納)"
```

---

## Task 5: API 関数を `Anonymous` 化 + `AuthenticatedTenant` 利用、`RequestTenant` 削除

**Files:**
- Modify: `src/EpcForwarder.Functions/Api/SessionQueryApi.cs`
- Modify: `src/EpcForwarder.Functions/Api/InventoryApi.cs`
- Delete: `src/EpcForwarder.Functions/Api/RequestTenant.cs`

- [ ] **Step 1: `SessionQueryApi` を変更**

3つの `[HttpTrigger(AuthorizationLevel.Function, ...)]` を `AuthorizationLevel.Anonymous` に変え、各関数のシグネチャに `FunctionContext context` を追加、`Validate` を差し替える。`Validate` を次に置換:

```csharp
    // tenant は認証ミドルウェアが解決済み。publicId のみ検証。
    private static IActionResult? Validate(FunctionContext context, Guid publicId, out int tenantId)
    {
        tenantId = 0;
        if (publicId == Guid.Empty)
        {
            return new BadRequestObjectResult("Invalid session id.");
        }

        if (!AuthenticatedTenant.TryGet(context, out tenantId))
        {
            // ミドルウェアを通っていれば通常ここには来ない(防御的に 401)。
            return new UnauthorizedResult();
        }

        return null;
    }
```

各 HttpTrigger メソッドを、`AuthorizationLevel.Anonymous` + 引数 `FunctionContext context` 追加 + `Validate(context, publicId, out var tenantId)` 呼び出しに更新する。例(GetSummary):

```csharp
    [Function("GetSummary")]
    public IActionResult GetSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{publicId:guid}/summary")] HttpRequest req,
        Guid publicId,
        FunctionContext context)
    {
        if (Validate(context, publicId, out var tenantId) is { } error)
        {
            return error;
        }
        // ...(既存の groupBy 分岐・queries 呼び出しはそのまま)
```

ファイル先頭に `using Microsoft.Azure.Functions.Worker;` は既にあるので、`using EpcForwarder.Functions.Auth;` を追加する。GetReconciliation / GetUnknown も同様に Anonymous + context 追加 + 新 Validate に。

- [ ] **Step 2: `InventoryApi` を変更**

2つの HttpTrigger を `Anonymous` 化。`RunAsync`/`Validate` を `FunctionContext` 経由に変更:

```csharp
    [Function("InventoryProvisional")]
    public Task<IActionResult> SendProvisional(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{publicId:guid}/inventory/provisional")] HttpRequest req,
        Guid publicId,
        FunctionContext context,
        CancellationToken ct = default) =>
        RunAsync(context, publicId, ct, (tenant, c) => inventory.SendProvisionalAsync(tenant, publicId, c));

    [Function("InventoryFinalize")]
    public Task<IActionResult> Finalize(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{publicId:guid}/inventory/finalize")] HttpRequest req,
        Guid publicId,
        FunctionContext context,
        CancellationToken ct = default) =>
        RunAsync(context, publicId, ct, (tenant, c) => inventory.FinalizeAndDeliverAsync(tenant, publicId, c));

    private static async Task<IActionResult> RunAsync(
        FunctionContext context,
        Guid publicId,
        CancellationToken ct,
        Func<int, CancellationToken, Task<InventoryPublishOutcome?>> publish)
    {
        if (Validate(context, publicId, out var tenantId) is { } error)
        {
            return error;
        }

        try
        {
            var outcome = await publish(tenantId, ct);
            if (outcome is null)
            {
                return new NotFoundResult();
            }

            return new OkObjectResult(new InventoryResultDto
            {
                Delivered = outcome.Delivered,
                StatusCode = outcome.Delivery?.StatusCode,
            });
        }
        catch (InvalidOperationException ex)
        {
            return new ConflictObjectResult(ex.Message);
        }
    }

    private static IActionResult? Validate(FunctionContext context, Guid publicId, out int tenantId)
    {
        tenantId = 0;
        if (publicId == Guid.Empty)
        {
            return new BadRequestObjectResult("Invalid session id.");
        }

        if (!AuthenticatedTenant.TryGet(context, out tenantId))
        {
            return new UnauthorizedResult();
        }

        return null;
    }
```

`using EpcForwarder.Functions.Auth;` を先頭に追加。

- [ ] **Step 3: `RequestTenant.cs` を削除**

Run:
```bash
git rm src/EpcForwarder.Functions/Api/RequestTenant.cs
```

- [ ] **Step 4: ビルド + 既存 Functions テスト確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build src/EpcForwarder.Functions/EpcForwarder.Functions.csproj -c Release 2>&1 | tail -4
dotnet test tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj -c Release 2>&1 | grep -iE "合格|失敗|passed|failed" | tail -3
```
Expected: ビルド 0 エラー/0 警告。既存 Functions テストが緑(`RequestTenant` を参照していたテストがあればコンパイルエラーになる → その場合は次ステップで対応)。

- [ ] **Step 5: `RequestTenant` 参照テストの更新(あれば)**

Run(参照を検索):
```bash
grep -rn "RequestTenant\|X-EPCF-Tenant" tests/ 2>/dev/null | tr -d '\r'
```
ヒットしたテストは、ヘッダ設定ではなく `FunctionContext` に `AuthenticatedTenant.Set(context, tenantId)` 済みの状態を渡す形へ更新する(該当テストの組み立て方に合わせる)。ヒットが無ければ何もしない。再度:
```bash
dotnet test tests/EpcForwarder.Functions.Tests/EpcForwarder.Functions.Tests.csproj -c Release 2>&1 | grep -iE "合格|失敗" | tail -2
```
Expected: 緑。

- [ ] **Step 6: コミット**

```bash
git add src/EpcForwarder.Functions/Api/
git commit -m "feat(functions): HTTP API を Anonymous 化し認証ミドルウェア由来の tenant を使用(X-EPCF-Tenant 廃止)"
```

---

## Task 6: External ID 構築 runbook

**Files:**
- Create: `docs/runbooks/entra-external-id.md`

- [ ] **Step 1: runbook を作成**

`docs/runbooks/entra-external-id.md`:

```markdown
# Entra External ID マルチテナント認証 構築手順

HTTP API は External ID(CIAM)が発行する JWT を検証する。実テナント構築は手動。

## 1. External ID テナント作成
Azure ポータル → Microsoft Entra External ID → 外部テナントを作成(CIAM)。

## 2. API アプリ登録
- アプリの登録 → 新規登録(名: epcf-api)。
- 「アプリケーション ID の URI」を設定(例: `api://epcf-api`)= これが **audience**。

## 3. カスタムユーザー属性
- ユーザー属性 → カスタム属性 `tenantId`(String)を作成。
- トークンに出すと `extension_<appid>_tenantId` というクレーム名になる(この実値を控える)。

## 4. ユーザーフロー
- サインアップ&サインインのユーザーフローを作成し、アプリに紐づけ。
- 属性 `tenantId` を「アプリケーションクレーム」に含める。

## 5. テストユーザー
- ユーザーを作成し、`tenantId = <SQL の dbo.tenant.code>`(例: `acme`)を設定。

## 6. メタデータ確認
- OIDC メタデータ: `https://<tenant>.ciamlogin.com/<tenant-id>/v2.0/.well-known/openid-configuration`
- issuer はメタデータの `issuer` 値。

## 7. Functions アプリ設定
以下を投入(環境変数は二重アンダースコア):
- `Auth__Issuer` = メタデータの issuer
- `Auth__Audience` = `api://epcf-api`
- `Auth__TenantClaim` = `extension_<appid>_tenantId`(手順3の実クレーム名)
- `Auth__MetadataAddress` = 手順6の URL

設定後 `az functionapp restart`。

## 8. 動作確認
External ID からアクセストークンを取得し:
```bash
curl -H "Authorization: Bearer <token>" https://<func>.azurewebsites.net/api/sessions/<sid>/summary
```
- トークン無し → 401、tenant 不明 → 403、別テナントのセッション → 404、正常 → 200。

> deploy.sh / bicep への Auth__* 投入自動化は将来対応(本実装はアプリ設定を読むのみ)。
```

- [ ] **Step 2: コミット**

```bash
git add docs/runbooks/entra-external-id.md
git commit -m "docs(runbook): Entra External ID 構築手順"
```

---

## Task 7: 全体ビルド + 全テスト確認

**Files:** なし(検証のみ)

- [ ] **Step 1: ソリューション全体ビルド**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build EpcForwarder.sln -c Release 2>&1 | tail -5
```
Expected: `0 個の警告` / `0 エラー`。

- [ ] **Step 2: 全テスト実行**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
for p in tests/EpcForwarder.Core.Tests tests/EpcForwarder.Functions.Tests tests/EpcForwarder.Infrastructure.Tests; do
  echo "=== $p ==="
  dotnet test "$p" -c Release --nologo 2>&1 | grep -iE "合格|失敗|成功" | tail -1
done
```
Expected: Core 101 / Functions 30(既存23 + JwtBearerValidator 7)/ Infrastructure 45(既存43 + SqlTenantLookup 2)、いずれも失敗 0。

- [ ] **Step 3: 完了確認**

Run:
```bash
git status -sb
git log --oneline -7 | cat
```
Expected: working tree clean。Task 1〜6 のコミットが並ぶ。

---

## Self-Review(計画作成者によるチェック結果)

- **Spec coverage:** §2 決定事項 → IdP/クレーム(code)/検証場所/Anonymous化・ヘッダ廃止 を Task 2-5 が実装。§4 コンポーネント → 全ファイルが Task 1-5 に対応(`ITenantLookup`/`SqlTenantLookup`=T1、`AuthOptions`/`JwtBearerValidator`/provider=T2-3、middleware/`AuthenticatedTenant`/Program=T4、API変更・`RequestTenant`削除=T5)。§5 エラー処理 → middleware の 401/403、API の 404 維持。§6 テスト → T1/T2 単体、T5 既存維持、T7 全体。§7 runbook → T6。§8 受け入れ基準 → T5(Anonymous/ヘッダ消滅)・T7(ビルド/テスト)。
- **Placeholder scan:** TODO/TBD 無し。全コードブロックは実コード。
- **Type consistency:** `ITenantLookup.ResolveId(string): int?`(T1定義 → T4 middleware で使用、一致)。`TokenValidationResult`/`AuthOutcome`(T2定義 → T4 で分岐、一致)。`AuthenticatedTenant.Set/TryGet(FunctionContext,...)`(T4定義 → T5 で使用、一致)。`JwtBearerValidator.ValidateAsync(string, CancellationToken): Task<TokenValidationResult>`(T2定義 → T4 使用、一致)。`AuthOptions` プロパティ(Issuer/Audience/TenantClaim/MetadataAddress)は T2/T3/T4 で一致。`ITokenValidationParametersProvider.GetAsync(CancellationToken)`(T2定義 → T3 実装/テストFixedProvider、一致)。
