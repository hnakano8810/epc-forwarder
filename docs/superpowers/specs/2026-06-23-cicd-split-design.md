# 詳細設計：CI/CD 分離（infra ↔ アプリパイプライン）

最終更新 2026-06-23。対象: 全部入りの `scripts/deploy.sh` を「Azureリソース作成（infra/IaC）」に痩せさせ、「migrate→publish→E2E（アプリ更新）」を GitHub Actions の CI/CD に移す。

## 背景・課題

現 `deploy.sh` は preflight→bicep→IoT接続→SQL FW→migrate→seed→publish→device→E2E を1本に束ねている。インフラ（稀に変更）とアプリ更新（毎コミット）はライフサイクルが異なるため分離する。加えて E2E が PR#25（`read`→`reads`）と PR#24（`X-EPCF-Tenant` 廃止・JWT必須）に追従できておらず、今のままでは末尾 E2E が必ず FAIL する。

## 決定事項

- **環境**: 単一 `epcf-rg`。テスト/本番の分離は**物理RG分割でなく `tenant_id`** で行う（データモデルが全テナントスコープのため）。E2E は専用テスト tenant 配下に閉じる。
- **トリガー**: PR では build+テストのみ。**main マージで deploy+E2E**。
- **Azure 認証**: GitHub Actions → **OIDC フェデレーション**（保存シークレット0）。SQL 接続文字列は実行時に **Key Vault（既存 `epcfkv...` の `SqlConnectionString`）から取得**。
- **E2E 合否**: クエリAPIは認証必須でCIハンズフリー困難 → **SQL直接チェック**（`verify` サブコマンド）。クエリAPIのE2Eは**CI対象外**（runbook §8 で手動）。

## スコープ

**やる:**
1. `scripts/deploy.sh` を infra-only に（migrate/seed/publish/device/E2E を除去）
2. `.github/workflows/ci-cd.yml`（test ジョブ + deploy ジョブ）
3. `EpcForwarder.Migrate` に `verify` サブコマンド追加 + Infrastructure テスト
4. E2E の D2C 送信を `reads` 形式へ（ワークフロー内）
5. `Auth__*` を bicep パラメータ化（値は空のまま＝テナント作成後に投入）
6. runbook 追記（OIDC セットアップ手順、deploy/CI 役割分担）

**やらない（手動・一回／対象外）:**
- OIDC 用 Azure AD アプリ＋フェデレーション資格情報＋ロール付与（手動1回。手順は runbook）
- External ID テナント作成（`entra-external-id.md`、CIAM は IaC 困難）
- クエリAPI の自動E2E（非対話トークンが要るため対象外。手動確認）

## 1. `deploy.sh`（infra-only）

残すステップ: `preflight` / `ensure_rg` / `deploy_bicep` / `set_iot_connection`。
除去: `open_sql_firewall` / `run_migrations_and_seed` / `publish_functions` / `ensure_device` / `run_e2e`。
`Auth__*` は **bicep が設定**（§5）するため deploy.sh には足さない。IoT接続文字列は実行時 `listKeys` 相当が要る（bicep だと linter 警告回避が面倒）ため当面 deploy.sh の `set_iot_connection` に残す。

- `func ... publish` はコードのみ発行し app 設定を消さないため、IoT接続・`Auth__*` を infra 側で設定して問題ない。
- `deploy.env` から不要になった項目（`SEED_WEBHOOK_URL`/`DEVICE_ID`）は CI 側（Actions variables/secrets）へ移す。`deploy.env.example` も更新。
- preflight の必須バイナリから `func` を外す（publish しないため）。`dotnet`/`curl` も不要なら外す。

## 2. GitHub Actions: `.github/workflows/ci-cd.yml`

```
on:
  pull_request:
  push: { branches: [main] }

concurrency: { group: epcf-deploy, cancel-in-progress: true }

jobs:
  test:               # PR + main
    - actions/checkout
    - actions/setup-dotnet (8.0.x)
    - dotnet build (TreatWarningsAsErrors 既定)
    - dotnet test Core / Functions / Infrastructure(Testcontainers, ubuntu の Docker)
  deploy:             # main のみ
    needs: test
    if: github.ref == 'refs/heads/main' && github.event_name == 'push'
    permissions: { id-token: write, contents: read }   # OIDC
    - actions/checkout
    - azure/login@v2 (client-id/tenant-id/subscription-id, OIDC)
    - az で bicep 出力(FUNC/IOT/SQLFQDN/SQLDB/KV)を取得
    - ランナー公開IPを SQL FW に一時追加（step で add、後続 always step で削除）
    - az keyvault secret show で SqlConnectionString 取得 → EPCF_SQL_CONNECTION
    - dotnet run --project tools/EpcForwarder.Migrate -- migrate
    - dotnet run --project tools/EpcForwarder.Migrate -- seed   (SEED_WEBHOOK_URL は secret)
    - func azure functionapp publish $FUNC --dotnet-isolated
    - az iot hub device-identity create（冪等、テストデバイス）
    - E2E: az iot device send-d2c-message で reads + complete → verify
    - 後始末: SQL FW 一時ルール削除（always）
```

- **必要な GitHub 設定**: variables=`AZURE_SUBSCRIPTION_ID`/`RG`/`PREFIX`、secrets=`AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`SEED_WEBHOOK_URL`。SQL パスワードは GH に置かず KV から取得。
- **OIDC ID のロール**: epcf-rg Contributor（publish/設定/FW）+ KV Secrets User（接続文字列取得）。

## 3. `verify` サブコマンド（`tools/EpcForwarder.Migrate`）

`Program.cs` の switch に `verify` を追加（既存 `migrate`/`seed` と同様 `EPCF_SQL_CONNECTION` を使用）。

- 使い方: `verify <session_id> <tenant_id> <expected_count>`
- 検証（Dapper、新クラス `Verifier`）:
  - `dbo.reading` の `(tenant_id, session_id, excluded=0)` 件数 == expected_count
  - `dbo.session` の該当 `status` == `forwarded`（=突合一致→配信到達）
  - いずれか不一致なら stderr にメッセージ＋**非0 exit**
- E2E 用なので最小限。SQL アクセスは既存 `SqlConnectionFactory`/Dapper を再利用、sqlcmd 不要。

## 4. E2E の `reads` 化（ワークフロー内）

D2C 送信を以下へ（旧 `kind:"read"` 単数は廃止済み）:
```json
{"kind":"reads","tenant":1,"session_id":"<sid>","business_key":"DEPLOY-E2E",
 "session_type":"shipment","resolve_sku":true,"device_id":"<dev>",
 "epcs":[{"epc":"302DB42318A0038000001231","read_at":"2026-01-01T00:00:00Z"}]}
```
続けて `{"kind":"complete","tenant":1,"session_id":"<sid>","expected_count":1}`。
その後 `verify <sid> <test_tenant_id> 1`。`X-EPCF-Tenant`/`?code=` ベースの HTTP 検証は廃止。

## 5. `Auth__*` の bicep パラメータ化

`infra/main.bicep`（+ `functions.bicep`）に `authIssuer`/`authAudience`/`authTenantClaim`/`authMetadataAddress` パラメータを追加し Functions app 設定 `Auth__Issuer` 等へ。`main.bicepparam` で既定は空文字。

- 空のままなら現状どおり middleware は fail-closed（401）= 挙動変化なし。
- External ID テナント作成後にパラメータへ値を入れて再デプロイすれば IaC で投入される。
- `Auth__*` は **bicep 一本化**（deploy.sh では設定しない）。IoT接続のみ deploy.sh に残す（§1）。

## 6. runbook 追記

- `docs/runbooks/deploy.md`: 「deploy.sh=infra のみ / アプリ更新・E2E は CI（main マージ）」の役割分担に更新。
- 新規 or 既存に **OIDC セットアップ手順**（AAD アプリ作成 → フェデレーション資格情報 `repo:hnakano8810/epc-forwarder:ref:refs/heads/main` → ロール付与）。
- `entra-external-id.md` は §7 が bicep パラメータ化された旨を追記。

## テスト・検証

- **ローカル検証可**: `verify` の Infrastructure テスト（Testcontainers: 取込済みsessionで成功 / 件数不一致・未forwardedで失敗）。`az bicep build` 0 警告。`dotnet build` 0 警告・全テスト緑。
- **ローカル検証不可**: ワークフロー実体（OIDC/Azure 実行）。初回 main マージで実証。YAML は構文・ジョブ依存・concurrency・権限をレビューで担保。

## 影響ファイル

- `scripts/deploy.sh`（痩せさせる）、`scripts/deploy.env.example`（項目整理）
- `.github/workflows/ci-cd.yml`（新規）
- `tools/EpcForwarder.Migrate/Program.cs`（verify 分岐）、`src/EpcForwarder.Infrastructure/Persistence/Verifier.cs`（新規）
- `tests/EpcForwarder.Infrastructure.Tests/VerifierTests.cs`（新規）
- `infra/main.bicep` / `infra/modules/functions.bicep` / `infra/main.bicepparam`（Auth__* パラメータ）
- `docs/runbooks/deploy.md` / `docs/runbooks/entra-external-id.md`
