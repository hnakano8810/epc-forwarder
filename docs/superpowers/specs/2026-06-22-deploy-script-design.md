# 設計: 再現性のある一括デプロイスクリプト `scripts/deploy.sh`

- 日付: 2026-06-22
- 状態: 設計承認済み(実装計画へ移行予定)
- 関連: PR #22(取込根因修正・実機E2E成功)、`docs/runbooks/deploy.md`(現手動手順)

## 1. 目的とゴール

**適切な権限を持つ作業者が、再現性のある形で・間違いなく・デプロイを完了できる**こと。

現状は `docs/runbooks/deploy.md` の手順を1コマンドずつ手で実行する必要があり、WSL/コピペでの改行混入による事故(`curl: Malformed input` 等)が頻発した。これを単一スクリプトに集約し、手作業のコマンド貼り付けをゼロにする。

「冪等(何度でも再実行)」そのものが目的ではなく、*正しく一回で完遂できる*ことが主目的。ただし途中失敗からの再実行・二度流しでも壊れないこと(再実行安全)は「間違いなく完了」を支えるため必須要件とする。

### 非ゴール / スコープ外(意図的)
- **teardown**(`az group delete`)は本スクリプトに含めない(誤実行リスク回避)。必要なら将来 `scripts/teardown.sh` を別途。
- **配信(webhook)到達の自動判定**は行わない。E2E は「送信した」+取込+クエリAPIまで(下記 §6)。
- **自動ログイン**はしない。未ログインなら停止して `az login` を促す。

## 2. 前提(作業者環境)
- bash(Linux / WSL / macOS)。
- 導入済み: `az`(>=2.81、`az bicep` 含む)、`func`(Azure Functions Core Tools v4)、`dotnet`(SDK 8)。
- **sqlcmd / docker は不要**(DB 適用は dotnet 移行ツールで行う、§4)。
- Azure に `az login` 済みで、対象サブスクリプションへの権限を持つ。

## 3. 成果物

| ファイル | 役割 |
|---|---|
| `scripts/deploy.sh` | 本体。preflight → deploy → IoT接続 → migration → seed → publish → device → E2E を一括・再実行安全 |
| `scripts/deploy.env.example` | 必要値の明文化テンプレート(作業者がコピーして埋める) |
| `scripts/deploy.env` | 実値。**`.gitignore` に追加**して非追跡 |
| `tools/EpcForwarder.Migrate/` | 新設 dotnet コンソール。`migrate` / `seed` サブコマンド |
| `docs/runbooks/deploy.md` | スクリプト主導に更新。手動手順は fallback として残し、stale な IoT-KV 手順を修正 |

## 4. `tools/EpcForwarder.Migrate`(新 console プロジェクト)

DB マイグレーションとシードを、テスト済みの既存コード経由で適用する。これにより作業者環境から sqlcmd / docker 依存を排除する(前提ツールは publish で既に必要な dotnet SDK のみ)。

- `EpcForwarder.Infrastructure` を ProjectReference。
- 接続文字列は環境変数(例 `EPCF_SQL_CONNECTION`)または引数で受領。ログには出さない。
- サブコマンド:
  - `migrate`: 既存 `MigrationRunner.Apply(connectionString)` を呼ぶ。埋め込み `db/migrations/*.sql` を名前順・冪等(IF NOT EXISTS)に適用。統合テスト(`MigrationSmokeTests`)と同一経路。
  - `seed`: 冪等 **MERGE** で投入。
    - `tenant`: `tenant_id=1, code='acme', name='Acme'`(IDENTITY のため MERGE で固定 id を保持)。
    - `destination`: `tenant_id=1, name='test-hook', url=<SEED_WEBHOOK_URL>, http_method='POST', schema_version='1', hmac_enabled=0, allow_provisional=1, is_active=1`。
    - `product`: `tenant_id=1, search_key=0x302DB42318A0038000000000(varbinary), sku='ITEM-AAA', item_code='ST-100', color='BLK', size='M', description='Sample'`。
    - webhook URL は環境変数 `SEED_WEBHOOK_URL` から。
- 失敗時は非0 exit。
- 列名・型は `db/migrations/0001_initial.sql` / `0002_destinations.sql` を正とする。

### テスト
- Testcontainers(`EpcForwarder.Infrastructure.Tests` に追加 or 移行ツール用テスト)で「`seed` を2回実行しても `tenant`/`destination`/`product` が各1行のまま」を検証(再実行安全の担保)。

## 5. `scripts/deploy.env.example`(キー一覧)

```
# Azure
SUBSCRIPTION_ID=
RG=epcf-rg
LOCATION=japaneast
PREFIX=epcf

# SQL 管理者
SQL_ADMIN=epcfadmin
SQL_PASSWORD=            # 16文字以上・英大小数記号

# シード / E2E
SEED_WEBHOOK_URL=        # 例 https://webhook.site/<uuid>
DEVICE_ID=handy-07
```

## 6. `scripts/deploy.sh` フロー

`set -euo pipefail`、`trap` で失敗ステップを明示。各ステップは関数化。秘密を扱う区間は `set +x`。

0. **preflight**: `az`/`func`/`dotnet` の存在確認。`deploy.env` を source(無ければ example を案内して停止)。`az account show` で未ログインなら明確なメッセージで停止。`az account set --subscription "$SUBSCRIPTION_ID"`。
1. **RG**: `az group create -n "$RG" -l "$LOCATION"`(冪等)。
2. **Bicep**: `az deployment group create -n main -f infra/main.bicep -p namePrefix=$PREFIX sqlAdminLogin=$SQL_ADMIN sqlAdminPassword=$SQL_PASSWORD`(冪等)。出力 `functionAppName`/`keyVaultName`/`sqlServerFqdn`/`sqlDatabaseName`/`iotHubName` を変数へ取得(`-o tsv | tr -d '\r\n'`)。
3. **IoT接続**: `az iot hub connection-string show --default-eventhub` で EH 互換接続文字列取得 → `az functionapp config appsettings set --settings IoTHubEventHubConnection=...`(上書き=冪等)→ `az functionapp restart`。
   - 注: 現行 bicep は `IoTHubEventHubConnection` をリテラル app 設定(プレースホルダ)として持つ。本ステップはそれを実値で上書きする(旧 runbook の KV シークレット方式は廃止)。
   - フォールバックルート/consumer group `functions` は bicep 済みのため手動設定は不要。
4. **SQL FW**: 作業者の現IP(`curl -s https://ifconfig.me | tr -d '\r\n'`)を許可。`az sql server firewall-rule create`(既存なら更新、create-or-update 相当で冪等扱い)。移行ツールが作業者マシンから接続するため。
5. **migrate**: deploy.sh が Bicep 出力(`sqlServerFqdn`/`sqlDatabaseName`)+ `SQL_ADMIN`/`SQL_PASSWORD` から管理者接続文字列を組み立て `EPCF_SQL_CONNECTION` に export → `dotnet run --project tools/EpcForwarder.Migrate -c Release -- migrate`。
6. **seed**: `dotnet run --project tools/EpcForwarder.Migrate -c Release -- seed`(`SEED_WEBHOOK_URL` を env で渡す)。
7. **publish**: `export DOTNET_ROOT/PATH` 後 `func azure functionapp publish "$FUNC" --dotnet-isolated`。
8. **device**: `handy-07` を冪等登録(`device-identity show` で存在確認→無ければ `create`)。
9. **E2E検証**(失敗で非0 exit):
   - host 温め(`curl /` が 200 になるまでリトライ)。
   - 毎回ユニークな `session_id`(uuid)を生成し、取込契約(`docs/design/ingestion-contract.md`)準拠の `read`(SKU解決される EPC `302DB42318A0038000001231`)+ `complete`(expected=1)を `az iot device send-d2c-message` で送信。
   - 関数キー取得(`az functionapp keys list --query functionKeys.default -o tsv | tr -d '\r\n'`)。
   - `summary` を 200 までポーリング(タイムアウト付)。アサート: `total_quantity==1`、`items[].sku=='ITEM-AAA'`、`unknown_count==0`。
   - `reconciliation` が `expected==received==1, match==true`。
   - 他テナント(`X-EPCF-Tenant: 2`)で `404`。
   - すべて成功なら最後に URL とサマリを表示して exit 0。

## 7. 再現性・再実行安全の仕組み

| ステップ | 再実行安全の担保 |
|---|---|
| RG / Bicep / appsettings / publish | 本来冪等(同名デプロイ・上書き) |
| migration | 埋め込み.sql が IF NOT EXISTS |
| seed | MERGE で重複行を作らない |
| device | 存在ガード |
| SQL FW | create-or-update 扱い |
| E2E | 毎回新規 session_id でクリーン |

## 8. 秘密・エラー処理

- `deploy.env` は `.gitignore` で非追跡。リポジトリには example のみ。
- SQL パスワード等は env 経由でのみ az / 移行ツールに渡す。標準出力・ログに出さない(該当区間 `set +x`)。
- `set -euo pipefail` + `trap 'echo "FAILED at step: $CURRENT_STEP"' ERR`。
- `-o tsv` 取得値は必ず `tr -d '\r\n'`(WSL の CR 混入対策、過去のハマり所)。

## 9. 受け入れ基準

- クリーンな RG に対し `./scripts/deploy.sh` 一発で、全リソース作成 → 取込+クエリ API の E2E アサートまで通り exit 0。
- 同スクリプトを既存環境に再実行しても破壊・重複なく exit 0。
- 途中失敗(例: ネットワーク断)後の再実行で完了できる。
- 作業者マシンに sqlcmd / docker が無くても完了できる。
- 既存の全テスト(165)+ 新規 seed 冪等テストが緑。
