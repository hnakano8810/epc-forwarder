# 設計: Entra External ID によるマルチテナント認証(HTTP API)

- 日付: 2026-06-23
- 状態: 設計承認済み(実装計画へ移行予定)
- 対象: HTTP クエリ/棚卸 API 5関数。取込(IoT)経路は対象外(デバイス認証のまま)

## 1. 目的

HTTP API のテナント決定を「**自己申告**」から「**認証済みトークン由来**」へ変える。

現状(`SessionQueryApi`/`InventoryApi`)は `AuthorizationLevel.Function`(全テナント共通の Functions キー)+ `X-EPCF-Tenant` ヘッダ(整数・検証なし)。キーを持つ者は誰でも任意テナントを名乗れる = テナント分離が認証で担保されていない。

これを **Entra External ID(CIAM)が発行する JWT を検証し、トークン内のテナントクレームから内部 `tenant_id` を導出**する方式に置換する。

## 2. 決定事項(確定)

- **IdP**: Microsoft Entra External ID(顧客/外部向けディレクトリ)。単一の External ID テナントを使用。
- **テナント表現**: ユーザーのカスタム属性(extension)にテナント識別子を保持し、トークンにクレームとして出す。**値は `tenant.code`(安定文字列)**。`tenant_id`(int)直値は使わない(ID基盤とDB idを疎結合に保つ)。クレーム名は External ID の拡張属性命名(`extension_<appid>_tenantId` 形式)に依存するため、コードは**設定 `Auth__TenantClaim` で受けたクレーム名**を読む(ハードコードしない)。
- **検証場所**: Functions(.NET 8 isolated worker)内の **JWT 検証ミドルウェア**。追加 Azure リソース不要。純ロジックで単体テスト可能。
- **Functions の認可レベル**: 5関数を `AuthorizationLevel.Anonymous` 化(JWT が認証の本体)。**`X-EPCF-Tenant` ヘッダは廃止**、`RequestTenant` は撤去。
- **スコープ**: コード実装+単体テスト。実 External ID テナントの構築・実トークンE2E は runbook 手順化のみ(本PRでは実構築しない)。

## 3. アーキテクチャ

```
端末/ブラウザ
  │ Authorization: Bearer <External ID access token(JWT)>
  ▼
Functions(isolated worker)
  │ AuthenticationMiddleware
  │   1. Authorization ヘッダから Bearer トークン抽出
  │   2. JwtBearerValidator で検証(issuer / audience / 期限 / 署名=JWKS)
  │   3. tenant クレーム(code)を取り出す
  │   4. ITenantLookup で code → tenant_id 解決
  │   5. 認証済み tenant_id を FunctionContext.Items に格納
  │      (失敗時は 401/403 を返し関数本体を実行しない)
  ▼
関数(SessionQueryApi / InventoryApi)
  │ AuthenticatedTenant.Get(context) で tenant_id 取得(ヘッダ参照は廃止)
  ▼
Core(tenant スコープでクエリ/操作)
```

取込(`IngestionFunction`, EventHub トリガー)はこの経路を通らず、変更しない。

## 4. コンポーネント(ファイル構成)

| 場所 | 責務 |
|---|---|
| `src/EpcForwarder.Functions/Auth/JwtBearerValidator.cs`(新規) | External ID トークン検証。issuer/audience/期限/署名(JWKS)を検証し、`ClaimsPrincipal` か検証結果を返す。OIDC メタデータ/JWKS をキャッシュ。検証パラメータは設定から注入(テスト時は差し替え可能) |
| `src/EpcForwarder.Functions/Auth/AuthOptions.cs`(新規) | `Issuer` / `Audience` / `TenantClaim`(実クレーム名。External ID では `extension_<appid>_tenantId`)/ `MetadataAddress` を保持する設定クラス。アプリ設定 `Auth__*` から束縛 |
| `src/EpcForwarder.Functions/Auth/AuthenticationMiddleware.cs`(新規) | `IFunctionsWorkerMiddleware`。HTTP 関数に対しトークン検証→tenant解決→`FunctionContext.Items["TenantId"]` 格納。失敗時は HTTP 応答(401/403)を直接書き未認証で本体を呼ばない。取込関数(非HTTP)はスキップ |
| `src/EpcForwarder.Functions/Auth/AuthenticatedTenant.cs`(新規) | `static bool TryGet(FunctionContext, out int tenantId)`。`RequestTenant` を置換 |
| `src/EpcForwarder.Core/Abstractions/Ports.cs`(追記) | `ITenantLookup { int? ResolveId(string code); }` |
| `src/EpcForwarder.Infrastructure/Persistence/SqlTenantLookup.cs`(新規) | `tenant.code → tenant_id` を引く `ITenantLookup` 実装。短命メモリキャッシュ(既存 `CachingSecretStore` と同程度の方針)で毎回のDB往復を避ける |
| `src/EpcForwarder.Infrastructure/Persistence/ServiceCollectionExtensions.cs`(変更) | `SqlTenantLookup` を DI 登録 |
| `src/EpcForwarder.Functions/Api/SessionQueryApi.cs`(変更) | `AuthorizationLevel.Anonymous` 化。`Validate` を「publicId 検証 + `AuthenticatedTenant.TryGet`」に変更 |
| `src/EpcForwarder.Functions/Api/InventoryApi.cs`(変更) | 同上 |
| `src/EpcForwarder.Functions/Api/RequestTenant.cs`(削除) | `AuthenticatedTenant` に置換 |
| `src/EpcForwarder.Functions/Program.cs`(変更) | ミドルウェア登録(`ConfigureFunctionsWorkerDefaults` のパイプライン)+ `AuthOptions` 束縛 + `JwtBearerValidator` DI |

NuGet: `Microsoft.IdentityModel.Protocols.OpenIdConnect` + `Microsoft.IdentityModel.JsonWebTokens`(署名検証/JWKS取得)。

## 5. データフローとエラー処理

| 状況 | HTTP 応答 |
|---|---|
| Authorization ヘッダ無し / Bearer でない | 401 |
| 署名不正 / issuer 不一致 / audience 不一致 / 期限切れ | 401 |
| 検証OKだが tenant クレーム欠落 | 403 |
| tenant クレームが未知(`ITenantLookup` が null) | 403 |
| 検証OK・tenant解決OK・対象セッションが別テナント | 404(既存どおり。情報漏洩防止) |
| 正常 | 200 |

- 401 は `WWW-Authenticate: Bearer` を付す。
- 現在の「ヘッダ欠落 → 400」は 401/403 に置き換わる。

## 6. テスト(単体中心)

- **`JwtBearerValidatorTests`**(新規): テスト用 RSA 鍵で自作トークンを生成し、検証パラメータ(issuer/audience/署名鍵)をテスト値に差し替えて検証:
  - 正常 → 成功・claims 取得
  - 期限切れ / issuer 不一致 / audience 不一致 / 署名鍵違い → 失敗(401相当)
  - tenant クレーム有/無
- **`SqlTenantLookupTests`**(新規, Testcontainers): `code → tenant_id` の解決と、未知 code で null、キャッシュ動作。
- **API レベル**: `AuthenticatedTenant.TryGet` の有無で 401/403/200 経路。既存の Functions.Tests パターン(Core.Tests の fake 再利用)に合わせ、`FunctionContext` に tenant を載せた/載せない状態でハンドラ分岐を検証。
- 既存全テスト(Core 101 / Functions 23 / Infrastructure 43)を緑維持。

## 7. External ID 構築手順(runbook)

`docs/runbooks/entra-external-id.md`(新規)に手動構築手順:
1. Entra External ID テナント作成(CIAM)。
2. API 用アプリ登録 → Application ID URI / audience を決定。
3. カスタムユーザー属性 `tenantId`(拡張属性 `extension_<appid>_tenantId`)を定義。
4. サインアップ/サインインのユーザーフロー作成、トークンに tenant 属性を含める(claim 設定)。
5. テストユーザーに `tenantId = <tenant.code>` を付与。
6. OIDC メタデータURL(`.../v2.0/.well-known/openid-configuration`)・issuer・audience を控える。
7. Functions アプリ設定へ `Auth__Issuer` / `Auth__Audience` / `Auth__TenantClaim` / `Auth__MetadataAddress` を投入(deploy.sh / bicep への反映は将来。本PRはローカル設定とテストまで)。

## 8. 受け入れ基準

- 5つの HTTP 関数が `Anonymous` で、未認証(トークン無/不正)は 401、tenant 不明は 403、別テナントは 404、正常は 200。
- `X-EPCF-Tenant` ヘッダおよび `RequestTenant` がコードから消えている。
- `JwtBearerValidator` がテスト鍵で全ケース緑。`SqlTenantLookup` の解決/キャッシュが緑。
- 既存全テストが緑、`dotnet build` 0警告(`TreatWarningsAsErrors=true`)。
- External ID 構築手順が runbook に記載され、必要なアプリ設定キーが明記されている。

## 9. スコープ外(再掲)

- 取込(IoT)経路の認証(デバイス認証のまま)。
- 実 External ID テナント構築・実トークンE2E(手順化のみ)。
- operator_id(操作者帰属)— 別テーマ。
- deploy.sh / bicep への `Auth__*` 投入自動化(将来)。
