# 再現性のある一括デプロイ `scripts/deploy.sh` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 適切な権限を持つ作業者が、単一スクリプトで EPC Forwarder を再現性をもって・間違いなく・E2E 検証まで通してデプロイできるようにする。

**Architecture:** DB マイグレーション/シードは新設の薄い dotnet コンソール `tools/EpcForwarder.Migrate` が担い、ロジック本体(`MigrationRunner` / 新 `Seeder`)は `EpcForwarder.Infrastructure` に置いて Testcontainers でテストする。`scripts/deploy.sh` が az/func/dotnet を順に呼び、`.env` で値を受け取り、取込+クエリAPI の E2E を exit code で合否判定する。sqlcmd / docker は不要(前提は dotnet SDK のみ)。

**Tech Stack:** bash, Azure CLI(az / az iot / az bicep), Azure Functions Core Tools v4(func), .NET 8(dotnet), Microsoft.Data.SqlClient + Dapper, Testcontainers.MsSql, xUnit。

**設計根拠:** `docs/superpowers/specs/2026-06-22-deploy-script-design.md`

---

## File Structure

| ファイル | 責務 |
|---|---|
| `src/EpcForwarder.Infrastructure/Persistence/Seeder.cs`(新規) | 冪等 MERGE で tenant/destination/product を投入する `Seeder.Apply(connStr, webhookUrl)` |
| `tests/EpcForwarder.Infrastructure.Tests/SeederTests.cs`(新規) | `Seeder` の再実行安全(2回実行で行が増えない)を Testcontainers で検証 |
| `tools/EpcForwarder.Migrate/EpcForwarder.Migrate.csproj`(新規) | コンソールツールのプロジェクト定義 |
| `tools/EpcForwarder.Migrate/Program.cs`(新規) | `migrate` / `seed` サブコマンドの薄い CLI。接続文字列/URL を env から取得し Infrastructure を呼ぶ |
| `EpcForwarder.sln`(変更) | 新ツールプロジェクトを追加 |
| `scripts/deploy.env.example`(新規) | 必要値のテンプレート |
| `.gitignore`(変更) | `scripts/deploy.env` を非追跡に |
| `scripts/deploy.sh`(新規) | 一括デプロイ本体(preflight → provisioning → publish → device → E2E) |
| `docs/runbooks/deploy.md`(変更) | スクリプト主導に更新。手動手順は fallback。stale な IoT-KV 手順を修正 |

---

## Task 1: `Seeder`(Infrastructure)を TDD で追加

冪等シードのロジック本体。`MigrationRunner` と同じく `EpcForwarder.Infrastructure.Persistence` に置く。

**Files:**
- Create: `src/EpcForwarder.Infrastructure/Persistence/Seeder.cs`
- Test: `tests/EpcForwarder.Infrastructure.Tests/SeederTests.cs`

- [ ] **Step 1: 失敗するテストを書く**

`tests/EpcForwarder.Infrastructure.Tests/SeederTests.cs` を新規作成。共有の `SqlServerFixture`(`[Collection("sql")]`)は他テストの蓄積状態を持つため、絶対件数ではなく「2回目の実行で件数が増えない」かつ「行が存在する」をアサートする。

```csharp
// tests/EpcForwarder.Infrastructure.Tests/SeederTests.cs
using Dapper;
using EpcForwarder.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EpcForwarder.Infrastructure.Tests;

[Collection("sql")]
public sealed class SeederTests(SqlServerFixture fx)
{
    private const string Url = "https://example.test/hook";

    [Fact]
    public void Seed_IsRepeatable_DoesNotDuplicateRows()
    {
        // 1回目
        Seeder.Apply(fx.ConnectionString, Url);
        var afterFirst = Counts();

        // 2回目(再実行安全)
        Seeder.Apply(fx.ConnectionString, Url);
        var afterSecond = Counts();

        Assert.Equal(afterFirst, afterSecond);                 // 増えない
        Assert.True(afterFirst.tenant >= 1);                   // 行は存在
        Assert.True(afterFirst.destination >= 1);
        Assert.True(afterFirst.product >= 1);
    }

    [Fact]
    public void Seed_UpdatesDestinationUrl_OnRerun()
    {
        Seeder.Apply(fx.ConnectionString, "https://old.test/hook");
        Seeder.Apply(fx.ConnectionString, "https://new.test/hook");

        using var conn = new SqlConnection(fx.ConnectionString);
        var url = conn.QuerySingle<string>(
            "SELECT url FROM dbo.destination WHERE tenant_id=1 AND name='test-hook'");
        Assert.Equal("https://new.test/hook", url);
    }

    private (int tenant, int destination, int product) Counts()
    {
        using var conn = new SqlConnection(fx.ConnectionString);
        var t = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.tenant WHERE tenant_id=1");
        var d = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.destination WHERE tenant_id=1 AND name='test-hook'");
        var p = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.product WHERE tenant_id=1 AND sku='ITEM-AAA'");
        return (t, d, p);
    }
}
```

- [ ] **Step 2: ビルドが失敗することを確認(`Seeder` 未定義)**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj -c Release 2>&1 | tail -5
```
Expected: コンパイルエラー `型または名前空間の名前 'Seeder' が見つかりません`(CS0103/CS0246)。

- [ ] **Step 3: `Seeder` を実装**

`src/EpcForwarder.Infrastructure/Persistence/Seeder.cs` を新規作成。webhook URL はパラメータ化(インジェクション回避)。tenant は IDENTITY のため `IDENTITY_INSERT` + `MERGE` で tenant_id=1 を固定。destination は `(tenant_id, name)` で一意扱い・再実行時 URL 更新。product は PK `(tenant_id, search_key)`。

```csharp
// src/EpcForwarder.Infrastructure/Persistence/Seeder.cs
using Microsoft.Data.SqlClient;

namespace EpcForwarder.Infrastructure.Persistence;

/// <summary>
/// 実機E2E/デモ用のシードを冪等に投入する。tenant(1)/destination(test-hook)/product(ITEM-AAA)。
/// 列定義は db/migrations/0001_initial.sql・0002_destinations.sql を正とする。
/// </summary>
public static class Seeder
{
    public static void Apply(string connectionString, string webhookUrl)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SeedSql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@url";
        p.Value = webhookUrl;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private const string SeedSql = @"
SET XACT_ABORT ON;
BEGIN TRAN;

-- tenant_id=1 を固定(IDENTITY のため IDENTITY_INSERT + MERGE)
SET IDENTITY_INSERT dbo.tenant ON;
MERGE dbo.tenant AS t
USING (SELECT 1 AS tenant_id, 'acme' AS code, 'Acme' AS name) AS s
ON (t.tenant_id = s.tenant_id)
WHEN NOT MATCHED THEN
  INSERT (tenant_id, code, name) VALUES (s.tenant_id, s.code, s.name);
SET IDENTITY_INSERT dbo.tenant OFF;

-- destination は (tenant_id, name) で一意扱い。再実行で URL を更新。
MERGE dbo.destination AS d
USING (SELECT 1 AS tenant_id, 'test-hook' AS name) AS s
ON (d.tenant_id = s.tenant_id AND d.name = s.name)
WHEN MATCHED THEN
  UPDATE SET url = @url, http_method = 'POST', schema_version = '1',
             hmac_enabled = 0, allow_provisional = 1, is_active = 1
WHEN NOT MATCHED THEN
  INSERT (tenant_id, name, url, http_method, schema_version, hmac_enabled, allow_provisional, is_active)
  VALUES (s.tenant_id, s.name, @url, 'POST', '1', 0, 1, 1);

-- product は PK (tenant_id, search_key)。EPC 302DB42318A0038000001231 のマスク後検索キー。
MERGE dbo.product AS p
USING (SELECT 1 AS tenant_id, CONVERT(varbinary(32), 0x302DB42318A0038000000000) AS search_key, 'ITEM-AAA' AS sku) AS s
ON (p.tenant_id = s.tenant_id AND p.search_key = s.search_key)
WHEN NOT MATCHED THEN
  INSERT (tenant_id, search_key, sku, item_code, color, size, description)
  VALUES (s.tenant_id, s.search_key, s.sku, 'ST-100', 'BLK', 'M', 'Sample');

COMMIT;
";
}
```

- [ ] **Step 4: テストが通ることを確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet test tests/EpcForwarder.Infrastructure.Tests/EpcForwarder.Infrastructure.Tests.csproj -c Release --filter "FullyQualifiedName~SeederTests" 2>&1 | grep -iE "合格|失敗|成功|passed|failed" | tail -3
```
Expected: `合格: 2`(失敗 0)。Docker(Testcontainers SQL)が必要。

- [ ] **Step 5: コミット**

```bash
git add src/EpcForwarder.Infrastructure/Persistence/Seeder.cs tests/EpcForwarder.Infrastructure.Tests/SeederTests.cs
git commit -m "feat(infra): 冪等シーダ Seeder を追加(tenant/destination/product)"
```

---

## Task 2: `tools/EpcForwarder.Migrate` コンソールツール

`migrate` / `seed` の薄い CLI ラッパー。ロジックは Infrastructure 側(Task 1 と既存 MigrationRunner)。接続文字列は env `EPCF_SQL_CONNECTION`、webhook URL は env `SEED_WEBHOOK_URL`。

**Files:**
- Create: `tools/EpcForwarder.Migrate/EpcForwarder.Migrate.csproj`
- Create: `tools/EpcForwarder.Migrate/Program.cs`
- Modify: `EpcForwarder.sln`

- [ ] **Step 1: プロジェクトファイルを作成**

`tools/EpcForwarder.Migrate/EpcForwarder.Migrate.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>EpcForwarder.Migrate</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\EpcForwarder.Infrastructure\EpcForwarder.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: `Program.cs` を実装**

`tools/EpcForwarder.Migrate/Program.cs`:

```csharp
// tools/EpcForwarder.Migrate/Program.cs
using EpcForwarder.Infrastructure.Persistence;

static string Require(string name)
{
    var v = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(v))
    {
        Console.Error.WriteLine($"ERROR: 環境変数 {name} が未設定です。");
        Environment.Exit(2);
    }
    return v!;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: EpcForwarder.Migrate <migrate|seed>");
    return 2;
}

try
{
    switch (args[0])
    {
        case "migrate":
            MigrationRunner.Apply(Require("EPCF_SQL_CONNECTION"));
            Console.WriteLine("migrate: done");
            return 0;
        case "seed":
            Seeder.Apply(Require("EPCF_SQL_CONNECTION"), Require("SEED_WEBHOOK_URL"));
            Console.WriteLine("seed: done");
            return 0;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    return 1;
}
```

- [ ] **Step 3: ソリューションへ追加**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet sln EpcForwarder.sln add tools/EpcForwarder.Migrate/EpcForwarder.Migrate.csproj
```
Expected: `プロジェクト ... が追加されました`。

- [ ] **Step 4: ビルドが通ることを確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build tools/EpcForwarder.Migrate/EpcForwarder.Migrate.csproj -c Release 2>&1 | tail -4
```
Expected: `ビルドに成功しました` / `0 エラー`。

- [ ] **Step 5: 引数なし実行で usage が出て非0で終わることを確認**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet run --project tools/EpcForwarder.Migrate -c Release 2>&1; echo "exit=$?"
```
Expected: `usage: EpcForwarder.Migrate <migrate|seed>` と `exit=2`。

- [ ] **Step 6: コミット**

```bash
git add tools/EpcForwarder.Migrate/ EpcForwarder.sln
git commit -m "feat(tools): DB 移行/シード CLI EpcForwarder.Migrate を新設"
```

---

## Task 3: `deploy.env.example` と `.gitignore`

**Files:**
- Create: `scripts/deploy.env.example`
- Modify: `.gitignore`

- [ ] **Step 1: テンプレートを作成**

`scripts/deploy.env.example`:

```bash
# scripts/deploy.env.example
# このファイルを scripts/deploy.env にコピーして実値を埋める(deploy.env は git 追跡外)。

# --- Azure ---
SUBSCRIPTION_ID=
RG=epcf-rg
LOCATION=japaneast
PREFIX=epcf

# --- SQL 管理者 ---
SQL_ADMIN=epcfadmin
SQL_PASSWORD=            # 16文字以上・英大文字小文字数字記号

# --- シード / E2E ---
SEED_WEBHOOK_URL=        # 例 https://webhook.site/<uuid>
DEVICE_ID=handy-07
```

- [ ] **Step 2: `.gitignore` に追記**

`.gitignore` の末尾へ以下を追加(既存内容は残す):

```
# デプロイ作業者のローカル秘密(テンプレートは deploy.env.example)
scripts/deploy.env
```

- [ ] **Step 3: deploy.env が無視されることを確認**

Run:
```bash
printf 'SQL_PASSWORD=secret\n' > scripts/deploy.env
git check-ignore scripts/deploy.env; echo "ignored-exit=$?"
git status --porcelain scripts/ | grep -v example || echo "(deploy.env は status に出ない=OK)"
rm -f scripts/deploy.env
```
Expected: `git check-ignore` が `scripts/deploy.env` を出力し `ignored-exit=0`。status に deploy.env が出ない。

- [ ] **Step 4: コミット**

```bash
git add scripts/deploy.env.example .gitignore
git commit -m "chore(scripts): deploy.env テンプレートと gitignore"
```

---

## Task 4: `scripts/deploy.sh` — preflight + provisioning(手順0〜8)

E2E 検証(手順9)は Task 5 で追加する。まずプロビジョニング〜publish〜device まで。

**Files:**
- Create: `scripts/deploy.sh`

- [ ] **Step 1: スクリプト本体を作成**

`scripts/deploy.sh`(E2E 検証関数 `run_e2e` は Task 5 で実装。ここでは呼び出しのみ TODO ではなく「未実装メッセージで停止しない」よう、Task 5 まで `main` から外しておく):

```bash
#!/usr/bin/env bash
# scripts/deploy.sh — EPC Forwarder 一括デプロイ(再現性・再実行安全)。
# 前提: bash, az(+az bicep, az iot), func v4, dotnet SDK 8。az login 済み。
# 値は scripts/deploy.env(scripts/deploy.env.example を参照)から読む。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CURRENT_STEP="init"
trap 'echo ">>> FAILED at step: $CURRENT_STEP" >&2' ERR

log() { echo "=== $* ==="; }

# --- 0. preflight ---
preflight() {
  CURRENT_STEP="preflight"
  for bin in az func dotnet curl; do
    command -v "$bin" >/dev/null 2>&1 || { echo "ERROR: '$bin' が見つかりません。" >&2; exit 1; }
  done
  if [ ! -f "$SCRIPT_DIR/deploy.env" ]; then
    echo "ERROR: $SCRIPT_DIR/deploy.env がありません。deploy.env.example をコピーして値を埋めてください。" >&2
    exit 1
  fi
  # shellcheck disable=SC1091
  set -a; . "$SCRIPT_DIR/deploy.env"; set +a
  : "${SUBSCRIPTION_ID:?deploy.env に SUBSCRIPTION_ID が必要}"
  : "${RG:?}" "${LOCATION:?}" "${PREFIX:?}" "${SQL_ADMIN:?}" "${SQL_PASSWORD:?}"
  : "${SEED_WEBHOOK_URL:?}" "${DEVICE_ID:?}"
  if ! az account show >/dev/null 2>&1; then
    echo "ERROR: 未ログインです。先に 'az login'(必要なら --tenant 指定)を実行してください。" >&2
    exit 1
  fi
  az account set --subscription "$SUBSCRIPTION_ID"
  log "preflight OK (sub=$SUBSCRIPTION_ID rg=$RG)"
}

# --- 1. リソースグループ ---
ensure_rg() {
  CURRENT_STEP="resource-group"
  az group create -n "$RG" -l "$LOCATION" -o none
  log "resource group ready"
}

# --- 2. Bicep デプロイ + 出力取得 ---
deploy_bicep() {
  CURRENT_STEP="bicep"
  set +x
  az deployment group create -g "$RG" -n main \
    -f "$REPO_ROOT/infra/main.bicep" \
    -p namePrefix="$PREFIX" sqlAdminLogin="$SQL_ADMIN" sqlAdminPassword="$SQL_PASSWORD" \
    -o none
  FUNC=$(az deployment group show -g "$RG" -n main --query properties.outputs.functionAppName.value -o tsv | tr -d '\r\n')
  SQLFQDN=$(az deployment group show -g "$RG" -n main --query properties.outputs.sqlServerFqdn.value -o tsv | tr -d '\r\n')
  SQLDB=$(az deployment group show -g "$RG" -n main --query properties.outputs.sqlDatabaseName.value -o tsv | tr -d '\r\n')
  IOT=$(az deployment group show -g "$RG" -n main --query properties.outputs.iotHubName.value -o tsv | tr -d '\r\n')
  log "bicep deployed: FUNC=$FUNC IOT=$IOT SQL=$SQLFQDN/$SQLDB"
}

# --- 3. IoT 接続文字列を app 設定へ ---
set_iot_connection() {
  CURRENT_STEP="iot-connection"
  local conn
  conn=$(az iot hub connection-string show -g "$RG" --hub-name "$IOT" --default-eventhub -o tsv | tr -d '\r\n')
  az functionapp config appsettings set -g "$RG" -n "$FUNC" \
    --settings "IoTHubEventHubConnection=$conn" -o none
  az functionapp restart -g "$RG" -n "$FUNC" -o none
  log "IoT connection set on $FUNC"
}

# --- 4. SQL ファイアウォール(作業者の現IP) ---
open_sql_firewall() {
  CURRENT_STEP="sql-firewall"
  local myip server
  myip=$(curl -s https://ifconfig.me | tr -d '\r\n')
  server="${SQLFQDN%%.*}"
  # create-or-update: 既存でも同名なら更新で冪等
  az sql server firewall-rule create -g "$RG" -s "$server" -n deploy-operator-ip \
    --start-ip-address "$myip" --end-ip-address "$myip" -o none
  log "SQL firewall allows $myip"
}

# --- 5/6. migrate + seed(dotnet ツール) ---
run_migrations_and_seed() {
  CURRENT_STEP="migrate-seed"
  export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
  set +x
  export EPCF_SQL_CONNECTION="Server=tcp:${SQLFQDN},1433;Database=${SQLDB};User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
  dotnet run --project "$REPO_ROOT/tools/EpcForwarder.Migrate" -c Release -- migrate
  SEED_WEBHOOK_URL="$SEED_WEBHOOK_URL" \
    dotnet run --project "$REPO_ROOT/tools/EpcForwarder.Migrate" -c Release -- seed
  unset EPCF_SQL_CONNECTION
  log "migrations + seed applied"
}

# --- 7. Functions 発行 ---
publish_functions() {
  CURRENT_STEP="publish"
  export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
  ( cd "$REPO_ROOT/src/EpcForwarder.Functions" && func azure functionapp publish "$FUNC" --dotnet-isolated )
  log "functions published"
}

# --- 8. デバイス登録(冪等) ---
ensure_device() {
  CURRENT_STEP="device"
  if az iot hub device-identity show --hub-name "$IOT" --device-id "$DEVICE_ID" -o none 2>/dev/null; then
    log "device $DEVICE_ID exists"
  else
    az iot hub device-identity create --hub-name "$IOT" --device-id "$DEVICE_ID" -o none
    log "device $DEVICE_ID created"
  fi
}

main() {
  preflight
  ensure_rg
  deploy_bicep
  set_iot_connection
  open_sql_firewall
  run_migrations_and_seed
  publish_functions
  ensure_device
  log "Provisioning complete. FUNC=$FUNC IOT=$IOT"
}

main "$@"
```

- [ ] **Step 2: 実行権限を付与**

Run:
```bash
chmod +x scripts/deploy.sh
ls -l scripts/deploy.sh
```
Expected: `-rwxr-xr-x` 等、実行ビットあり。

- [ ] **Step 3: 構文チェック(live 実行はしない)**

Run:
```bash
bash -n scripts/deploy.sh && echo "syntax OK"
command -v shellcheck >/dev/null 2>&1 && shellcheck scripts/deploy.sh || echo "(shellcheck 未導入: スキップ)"
```
Expected: `syntax OK`。shellcheck があれば致命的エラーが無いこと(情報レベルの警告は可)。

- [ ] **Step 4: preflight の早期失敗を確認(deploy.env 無し)**

Run:
```bash
[ -f scripts/deploy.env ] && mv scripts/deploy.env scripts/deploy.env.bak || true
./scripts/deploy.sh 2>&1 | head -3; echo "exit=${PIPESTATUS[0]}"
[ -f scripts/deploy.env.bak ] && mv scripts/deploy.env.bak scripts/deploy.env || true
```
Expected: `deploy.env がありません` のメッセージと `exit=1`(az を一切叩く前に停止)。

- [ ] **Step 5: コミット**

```bash
git add scripts/deploy.sh
git commit -m "feat(scripts): deploy.sh プロビジョニング(preflight〜publish〜device)"
```

---

## Task 5: `scripts/deploy.sh` — E2E 検証(手順9)

取込+クエリAPI を exit code で合否判定する `run_e2e` を追加し、`main` から呼ぶ。毎回ユニークな session_id を使い再実行安全にする。

**Files:**
- Modify: `scripts/deploy.sh`

- [ ] **Step 1: `run_e2e` 関数を追加**

`ensure_device()` 関数の直後(`main()` の前)に以下を挿入:

```bash
# --- 9. E2E 検証(取込 + クエリAPI)。失敗で非0 exit ---
run_e2e() {
  CURRENT_STEP="e2e"
  local host key sid code body
  host=$(az functionapp show -g "$RG" -n "$FUNC" --query defaultHostName -o tsv | tr -d '\r\n')
  set +x
  key=$(az functionapp keys list -g "$RG" -n "$FUNC" --query functionKeys.default -o tsv | tr -d '\r\n')

  # host 温め(200 になるまで最大 ~60s)
  for _ in $(seq 1 12); do
    code=$(curl -s -o /dev/null -w "%{http_code}" "https://$host/" | tr -d '\r\n')
    [ "$code" = "200" ] && break
    sleep 5
  done

  # 毎回新規 session_id(再実行安全)
  sid=$(cat /proc/sys/kernel/random/uuid)
  log "E2E session=$sid"

  # read(SKU 解決される EPC)+ complete(expected=1)を D2C 送信
  az iot device send-d2c-message --hub-name "$IOT" --device-id "$DEVICE_ID" \
    --data "{\"kind\":\"read\",\"tenant\":1,\"session_id\":\"$sid\",\"business_key\":\"DEPLOY-E2E\",\"session_type\":\"shipment\",\"resolve_sku\":true,\"epc\":\"302DB42318A0038000001231\",\"device_id\":\"$DEVICE_ID\",\"read_at\":\"2026-01-01T00:00:00Z\"}" -o none
  az iot device send-d2c-message --hub-name "$IOT" --device-id "$DEVICE_ID" \
    --data "{\"kind\":\"complete\",\"tenant\":1,\"session_id\":\"$sid\",\"expected_count\":1}" -o none

  # summary を 200 までポーリング(最大 ~120s)
  local url="https://$host/api/sessions/$sid/summary?code=$key"
  code=000
  for _ in $(seq 1 24); do
    code=$(curl -s -o /tmp/epcf_e2e_summary.json -w "%{http_code}" -H "X-EPCF-Tenant: 1" "$url" | tr -d '\r\n')
    [ "$code" = "200" ] && break
    sleep 5
  done
  [ "$code" = "200" ] || { echo "E2E FAIL: summary が 200 になりません (last=$code)" >&2; return 1; }

  body=$(cat /tmp/epcf_e2e_summary.json)
  echo "summary: $body"
  echo "$body" | grep -q '"total_quantity":1' || { echo "E2E FAIL: total_quantity!=1" >&2; return 1; }
  echo "$body" | grep -q '"sku":"ITEM-AAA"' || { echo "E2E FAIL: SKU が ITEM-AAA でない" >&2; return 1; }
  echo "$body" | grep -q '"unknown_count":0' || { echo "E2E FAIL: unknown_count!=0" >&2; return 1; }

  # reconciliation: expected=received=1, match=true
  body=$(curl -s -H "X-EPCF-Tenant: 1" "https://$host/api/sessions/$sid/reconciliation?code=$key")
  echo "reconciliation: $body"
  echo "$body" | grep -q '"match":true' || { echo "E2E FAIL: reconciliation match!=true" >&2; return 1; }

  # 他テナントは 404
  code=$(curl -s -o /dev/null -w "%{http_code}" -H "X-EPCF-Tenant: 2" "$url" | tr -d '\r\n')
  [ "$code" = "404" ] || { echo "E2E FAIL: 他テナントが 404 でない (got=$code)" >&2; return 1; }

  rm -f /tmp/epcf_e2e_summary.json
  log "E2E PASSED (session=$sid)"
}
```

- [ ] **Step 2: `main` の最後に `run_e2e` を追加**

`main()` 内の `log "Provisioning complete..."` 行を、以下に置き換える:

```bash
  run_e2e
  log "ALL DONE. FUNC=$FUNC IOT=$IOT  E2E=PASSED"
```

- [ ] **Step 3: 構文チェック**

Run:
```bash
bash -n scripts/deploy.sh && echo "syntax OK"
command -v shellcheck >/dev/null 2>&1 && shellcheck scripts/deploy.sh || echo "(shellcheck 未導入: スキップ)"
```
Expected: `syntax OK`。

- [ ] **Step 4: 実機での通し確認(作業者/セッションで実行)**

> これは live 統合確認。`scripts/deploy.env` を用意し、Azure にログイン済みの環境で実行する。

Run:
```bash
./scripts/deploy.sh 2>&1 | tail -20; echo "exit=${PIPESTATUS[0]}"
```
Expected: 最後に `E2E PASSED` と `ALL DONE ... E2E=PASSED`、`exit=0`。二度目の実行も同様に `exit=0`(再実行安全)。

- [ ] **Step 5: コミット**

```bash
git add scripts/deploy.sh
git commit -m "feat(scripts): deploy.sh に取込+クエリAPI の E2E 合否判定を追加"
```

---

## Task 6: runbook をスクリプト主導に更新

**Files:**
- Modify: `docs/runbooks/deploy.md`

- [ ] **Step 1: 冒頭にスクリプト手順を追記し、stale 箇所を修正**

`docs/runbooks/deploy.md` の先頭(タイトル行 `# EPC Forwarder デプロイ＋実機E2E 手順書` の直後)に、次の節を挿入する:

```markdown

## 推奨: 一括スクリプト

通常は単一スクリプトで全工程(デプロイ→IoT接続→マイグレーション→シード→発行→デバイス→E2E検証)を実行できる。

```bash
cp scripts/deploy.env.example scripts/deploy.env
# scripts/deploy.env を編集(SUBSCRIPTION_ID / SQL_PASSWORD / SEED_WEBHOOK_URL 等)
az login            # 未ログインなら(必要に応じ --tenant)
./scripts/deploy.sh
```

成功すると最後に `E2E PASSED` を表示し exit 0。途中失敗からの再実行・二度流しでも安全(再実行安全)。
フォールバックルートと consumer group `functions` は Bicep に含まれるため手動設定は不要。
DB マイグレーション/シードは `tools/EpcForwarder.Migrate`(dotnet)で適用するため **sqlcmd / docker は不要**。

以下の手動手順は、スクリプトを使わずステップ単位で確認・復旧したい場合のフォールバックである。
```

- [ ] **Step 2: 手順3(IoT 接続)の stale な KV 方式注記を修正**

`docs/runbooks/deploy.md` の手順3末尾にある一文:

```
> Functions アプリ設定 `IoTHubEventHubConnection` は KV 参照で本シークレットを指す。設定後、アプリ再起動で反映:
```

を、次に置き換える:

```
> 現行 Bicep では `IoTHubEventHubConnection` はリテラルの app 設定(初期はプレースホルダ)。本手順では実値を app 設定へ直接上書きする(KV シークレット方式は廃止)。設定後、アプリ再起動で反映:
```

- [ ] **Step 3: コミット**

```bash
git add docs/runbooks/deploy.md
git commit -m "docs(runbook): deploy.sh 主導に更新 + IoT接続のstale記述を修正"
```

---

## Task 7: 全体テストの最終確認

**Files:** なし(検証のみ)

- [ ] **Step 1: ソリューション全体ビルド**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
dotnet build EpcForwarder.sln -c Release 2>&1 | tail -5
```
Expected: `ビルドに成功しました` / `0 エラー`(新ツール含む)。

- [ ] **Step 2: 全テスト実行(165 + 新規 Seeder 2件)**

Run:
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
for p in tests/EpcForwarder.Core.Tests tests/EpcForwarder.Functions.Tests tests/EpcForwarder.Infrastructure.Tests; do
  echo "=== $p ==="
  dotnet test "$p" -c Release --nologo 2>&1 | grep -iE "合格|失敗|成功|passed|failed" | tail -2
done
```
Expected: Core 101 / Functions 23 / Infrastructure 43(既存41 + Seeder 2)、いずれも失敗 0。

- [ ] **Step 3: 完了報告**

新規・変更ファイルがコミット済みであることを確認:
```bash
git status -sb
git log --oneline -7
```
Expected: working tree clean。Task 1〜6 の 6 コミットが並ぶ。

---

## Self-Review(計画作成者によるチェック結果)

- **Spec coverage:** §3 成果物 → 全ファイルが Task 1〜6 に対応。§4 移行ツール → Task 1(Seeder)+ Task 2(CLI)。§5 .env → Task 3。§6 フロー手順0〜9 → Task 4(0〜8)+ Task 5(9)。§7 再実行安全 → Seeder MERGE(Task1)/device ガード・FW create/新session_id(Task4,5)。§8 秘密・エラー → `set +x`・`tr -d`・`trap ERR`(Task4,5)。§9 受け入れ基準 → Task 5 Step4・Task 7。
- **Placeholder scan:** TODO/TBD 無し。全コードブロックは実コード。E2E 関数は Task 5 で完全実装(Task 4 では呼ばない)。
- **Type consistency:** `MigrationRunner.Apply(string)`(既存・確認済)、`Seeder.Apply(string, string)`(Task1 定義 → Task2 で同シグネチャ呼び出し)、env 名 `EPCF_SQL_CONNECTION`/`SEED_WEBHOOK_URL` は Task2 Program.cs と Task4 deploy.sh で一致。Bicep 出力名 `functionAppName`/`sqlServerFqdn`/`sqlDatabaseName`/`iotHubName` は infra/main.bicep の outputs と一致。
