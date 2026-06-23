# EPC Forwarder デプロイ＋実機E2E 手順書

## 役割分担(重要)

ライフサイクルの違いで2つに分離している:

- **インフラ(稀に変更)= `scripts/deploy.sh`**: Azureリソース作成 + リソース間結線(RG / Bicep / IoT接続のapp設定)まで。
- **アプリ更新・E2E(毎マージ)= GitHub Actions `.github/workflows/ci-cd.yml`**: `main` マージで migrate → seed → publish → E2E(SQL直接 `verify`)。PR では build + テストのみ。

## A. インフラ プロビジョニング(deploy.sh)

```bash
cp scripts/deploy.env.example scripts/deploy.env
# scripts/deploy.env を編集(SUBSCRIPTION_ID / SQL_PASSWORD、External ID 作成後は AUTH_*)
az login            # 未ログインなら(必要に応じ --tenant)
./scripts/deploy.sh
```
リソース作成 + IoT接続のapp設定までを再実行安全に行う(`INFRA DONE` 表示で完了)。フォールバックルート・consumer group `functions`・`Auth__*` は Bicep に含まれる。**migrate/publish/E2E は実行しない(CI の担当)**。

## B. アプリ更新・E2E(GitHub Actions)

`main` にマージされると `ci-cd.yml` の deploy ジョブが migrate(0003含む全適用)→ seed(テストtenantのフィクスチャ)→ publish → E2E を実行。E2E は認証必須のクエリAPIに依存せず、`reads`+`complete` を D2C 送信して `EpcForwarder.Migrate verify <sid> <tenant> <count>`(SQL直接: 取込件数＋`session.status=forwarded`)で合否判定する。

### B-1. 初回のみ: OIDC セットアップ(手動・一回)
GitHub Actions が保存シークレット無しで Azure にログインするための連合資格情報を作る。

```bash
# 1) アプリ登録
APP_ID=$(az ad app create --display-name epcf-github-oidc --query appId -o tsv)
az ad sp create --id "$APP_ID"
# 2) 連合資格情報(main ブランチ push 用)
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name":"gh-main",
  "issuer":"https://token.actions.githubusercontent.com",
  "subject":"repo:hnakano8810/epc-forwarder:ref:refs/heads/main",
  "audiences":["api://AzureADTokenExchange"]
}'
# 3) ロール付与: epcf-rg Contributor + Key Vault Secrets User(SqlConnectionString 取得用)
SP_OID=$(az ad sp show --id "$APP_ID" --query id -o tsv)
RGID=$(az group show -n epcf-rg --query id -o tsv)
az role assignment create --assignee-object-id "$SP_OID" --assignee-principal-type ServicePrincipal \
  --role Contributor --scope "$RGID"
KVID=$(az keyvault show -n <epcfkv...> --query id -o tsv)
az role assignment create --assignee-object-id "$SP_OID" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "$KVID"
```
GitHub 側に登録(Settings → Secrets and variables → Actions):
- **Variables**: `AZURE_SUBSCRIPTION_ID` / `RG`(=epcf-rg) / `PREFIX`(=epcf)
- **Secrets**: `AZURE_CLIENT_ID`(=$APP_ID) / `AZURE_TENANT_ID` / `SEED_WEBHOOK_URL`
- SQL パスワードは GitHub に置かない(CI は Key Vault の `SqlConnectionString` から取得)。

---

以下の手動手順は、CI を使わずステップ単位で確認・復旧したい場合のフォールバックである(migrate/publish/E2E は通常 CI が実行)。
DB マイグレーション/シードは `tools/EpcForwarder.Migrate`(dotnet)で適用するため **sqlcmd / docker は不要**。

> 本手順は **ユーザーが実行**する(Claude は Azure へデプロイしない)。各コマンドは `!` プレフィックスでこのセッションから実行できる。
> 前提: `az`(>=2.81)/`az bicep` ログイン済、`func`(Azure Functions Core Tools v4)、`sqlcmd`(または `go-sqlcmd`)、`dotnet`(SDK 8、publish 用)。

## 0. 変数
```bash
RG=epcf-rg
LOCATION=japaneast
PREFIX=epcf
SQL_ADMIN=epcfadmin
SQL_PASSWORD='<強いパスワードを設定>'   # 16文字以上・英大小数記号
```

## 1. ログイン＆リソースグループ
```bash
az login
az account set --subscription "<サブスクリプションID>"
az group create -n "$RG" -l "$LOCATION"
```

## 2. Bicep デプロイ
```bash
az deployment group create \
  -g "$RG" \
  -f infra/main.bicep \
  -p namePrefix="$PREFIX" -p sqlAdminLogin="$SQL_ADMIN" -p sqlAdminPassword="$SQL_PASSWORD"
```
完了後、出力を変数へ:
```bash
FUNC=$(az deployment group show -g "$RG" -n main --query properties.outputs.functionAppName.value -o tsv)
KV=$(az deployment group show -g "$RG" -n main --query properties.outputs.keyVaultName.value -o tsv)
SQLFQDN=$(az deployment group show -g "$RG" -n main --query properties.outputs.sqlServerFqdn.value -o tsv)
SQLDB=$(az deployment group show -g "$RG" -n main --query properties.outputs.sqlDatabaseName.value -o tsv)
IOT=$(az deployment group show -g "$RG" -n main --query properties.outputs.iotHubName.value -o tsv)
echo "FUNC=$FUNC KV=$KV IOT=$IOT SQLFQDN=$SQLFQDN DB=$SQLDB"
```

## 3. IoT Hub の EventHub 互換接続文字列を app 設定へ投入
```bash
# 組込みエンドポイント(events)の EH 互換接続文字列を取得
IOT_EH_CONN=$(az iot hub connection-string show -g "$RG" --hub-name "$IOT" --default-eventhub -o tsv)
# Functions の app 設定 IoTHubEventHubConnection(初期はプレースホルダ)を実値で上書き
az functionapp config appsettings set -g "$RG" -n "$FUNC" --settings "IoTHubEventHubConnection=$IOT_EH_CONN" -o none
echo "IoTHubEventHubConnection set."
```
> 現行 Bicep では `IoTHubEventHubConnection` はリテラルの app 設定(初期はプレースホルダ)。本手順では実値を app 設定へ直接上書きする(KV シークレット方式は廃止)。設定後、アプリ再起動で反映:
```bash
az functionapp restart -g "$RG" -n "$FUNC"
```

## 4. DB マイグレーション適用
クライアント IP を一時許可(sqlcmd 用):
```bash
MYIP=$(curl -s https://ifconfig.me)
az sql server firewall-rule create -g "$RG" -s "${SQLFQDN%%.*}" -n myip --start-ip-address "$MYIP" --end-ip-address "$MYIP"
```
マイグレーション(冪等):
```bash
sqlcmd -S "tcp:$SQLFQDN,1433" -d "$SQLDB" -U "$SQL_ADMIN" -P "$SQL_PASSWORD" -N -C -i db/migrations/0001_initial.sql
sqlcmd -S "tcp:$SQLFQDN,1433" -d "$SQLDB" -U "$SQL_ADMIN" -P "$SQL_PASSWORD" -N -C -i db/migrations/0002_destinations.sql
```
確認:
```bash
sqlcmd -S "tcp:$SQLFQDN,1433" -d "$SQLDB" -U "$SQL_ADMIN" -P "$SQL_PASSWORD" -N -C -Q "SELECT name FROM sys.tables ORDER BY name;"
```
(`tenant` `product` `session` `reading` `snapshot` `destination` `destination_header` `mask` が並ぶ)

## 5. Functions 発行
```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
func azure functionapp publish "$FUNC" --dotnet-isolated
```
HTTP 関数の URL とキーを確認:
```bash
az functionapp function keys list -g "$RG" -n "$FUNC" --function-name GetSummary -o table || true
FUNC_HOST=$(az functionapp show -g "$RG" -n "$FUNC" --query defaultHostName -o tsv)
FUNC_KEY=$(az functionapp keys list -g "$RG" -n "$FUNC" --query functionKeys.default -o tsv)
echo "https://$FUNC_HOST/api/..."
```

## 6. シードデータ(テナント＋宛先＋商品)
受信側 Webhook は任意のテスト用エンドポイント(例: https://webhook.site の一意URL)を使う。
実スキーマ(`0001`/`0002`)準拠の列名を使う。`tenant.tenant_id` は IDENTITY のため `IDENTITY_INSERT` で tenant_id=1 を固定。`destination` は `http_method`/`is_active`/`allow_provisional`、`product.search_key` は `Sgtin96` マスク後検索キー(例 EPC `302DB42318A0038000001231` のマスク値 `0x302DB42318A0038000000000`)。
```bash
HOOK_URL='https://webhook.site/<your-uuid>'
sqlcmd -S "tcp:$SQLFQDN,1433" -d "$SQLDB" -U "$SQL_ADMIN" -P "$SQL_PASSWORD" -N -C -Q "
SET IDENTITY_INSERT dbo.tenant ON;
INSERT INTO dbo.tenant (tenant_id, code, name) VALUES (1, 'acme', 'Acme');
SET IDENTITY_INSERT dbo.tenant OFF;
INSERT INTO dbo.destination (tenant_id, name, url, http_method, schema_version, hmac_enabled, allow_provisional, is_active)
  VALUES (1, 'test-hook', '$HOOK_URL', 'POST', '1', 0, 1, 1);
INSERT INTO dbo.product (tenant_id, search_key, sku, item_code, color, size, description)
  VALUES (1, CONVERT(varbinary(32), 0x302DB42318A0038000000000), 'ITEM-AAA', 'ST-100', 'BLK', 'M', 'Sample');
"
```
> `tenant`/`reading`/`session` に tenant への FK は無いため tenant 行は必須ではないが、整合のため投入する。列定義に疑義があれば `db/migrations/0001_initial.sql` / `0002_destinations.sql` を正とする。

## 7. デバイス登録（取込テスト用）
```bash
az iot hub device-identity create --hub-name "$IOT" --device-id handy-07
DEV_CONN=$(az iot hub device-identity connection-string show --hub-name "$IOT" --device-id handy-07 -o tsv)
```

## 8. 実機E2E
### 8.1 取込(reads → complete)→ 自動配信(伝票)
`az iot device send-d2c-message` でデバイスメッセージ(取込契約 `docs/design/ingestion-contract.md`)を送る。**取込は `reads`(EPC配列)形式**(単数 `read` は廃止):
```bash
# reads(EPC配列。SKU解決される EPC を1件)
az iot device send-d2c-message --hub-name "$IOT" --device-id handy-07 \
  --data '{"kind":"reads","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001","business_key":"DN-1","session_type":"shipment","resolve_sku":true,"device_id":"handy-07","epcs":[{"epc":"302DB42318A0038000001231","read_at":"2026-06-19T00:00:01Z"}]}'
# complete(expected=1)
az iot device send-d2c-message --hub-name "$IOT" --device-id handy-07 \
  --data '{"kind":"complete","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001","expected_count":1}'
```
SQL 直接で確認するなら(CI と同じ判定): `EPCF_SQL_CONNECTION=... dotnet run --project tools/EpcForwarder.Migrate -- verify 9c3a8f10-0000-0000-0000-000000000001 1 1`。
確認:
- webhook.site に確定スナップショット POST(`is_final:true`, `items:[{sku:ITEM-AAA,quantity:1}]`)が届く。
- App Insights のトレースに `Completion session=... delivered=True`。
```bash
az monitor app-insights query -g "$RG" --apps "${PREFIX}-appi" \
  --analytics-query "traces | where message contains 'Completion' | top 5 by timestamp desc" -o table || \
  echo "(App Insights クエリは数分の取込遅延あり)"
```

### 8.2 クエリAPI(端末プル) ※認証必須・CI対象外
PR#24 以降、クエリAPIは **Entra External ID の JWT ベアラトークン必須**(`X-EPCF-Tenant` ヘッダ・`?code=` は廃止)。tenant はトークンの `tenantId` クレームから解決する。External ID テナント未構築だと **fail-closed で 401**。構築は `entra-external-id.md`。トークン取得はユーザーフロー(対話)のため CI E2E では検証せず、ここで手動確認する:
```bash
SID=9c3a8f10-0000-0000-0000-000000000001
TOKEN='<External ID で取得したアクセストークン>'
curl -s "https://$FUNC_HOST/api/sessions/$SID/summary"        -H "Authorization: Bearer $TOKEN" | jq .
curl -s "https://$FUNC_HOST/api/sessions/$SID/reconciliation" -H "Authorization: Bearer $TOKEN" | jq .
curl -s "https://$FUNC_HOST/api/sessions/$SID/unknown"        -H "Authorization: Bearer $TOKEN" | jq .
# トークン無し → 401、別テナントのセッション → 404
curl -s -o /dev/null -w "%{http_code}\n" "https://$FUNC_HOST/api/sessions/$SID/summary"
```
期待: summary は `total_quantity:1` / `items:[{sku:ITEM-AAA,quantity:1}]` / `unknown_count:0`、reconciliation は `expected:1,received:1,match:true`、トークン無しは `401`。

### 8.3 棚卸(HTTPトリガー)
棚卸セッションへ read を積んでから:
```bash
INV=11111111-0000-0000-0000-000000000001
az iot device send-d2c-message --hub-name "$IOT" --device-id handy-07 \
  --data '{"kind":"read","tenant":1,"session_id":"'"$INV"'","business_key":"INV-1","session_type":"inventory","resolve_sku":true,"epc":"302DB42318A0038000001231","device_id":"handy-07","location":{"l1":"DC","l2":"2F","l3":"A-01"},"read_at":"2026-06-19T00:01:00Z"}'
# 仮確定(open維持)
curl -s -X POST "https://$FUNC_HOST/api/sessions/$INV/inventory/provisional?code=$FUNC_KEY" -H "X-EPCF-Tenant: 1" | jq .
# ロケ別 summary
curl -s "https://$FUNC_HOST/api/sessions/$INV/summary?groupBy=location&code=$FUNC_KEY" -H "X-EPCF-Tenant: 1" | jq .
# 確定(forwarded)
curl -s -X POST "https://$FUNC_HOST/api/sessions/$INV/inventory/finalize?code=$FUNC_KEY" -H "X-EPCF-Tenant: 1" | jq .
```
期待: provisional/finalize は `{delivered:true,status_code:200}`、ロケ別 summary に `A-01` グループ、webhook.site に仮確定(`is_final:false`)と確定(`is_final:true`)が届く。

## 9. 後片付け
```bash
az group delete -n "$RG" --yes --no-wait
```

## トラブルシュート
- **KV参照が解決されない(アプリが起動失敗)**: Functions MI への Key Vault Secrets User 割当の伝播待ち(数分)。`az functionapp restart` 後再確認。
- **取込が反映されない**: IoT Hub→Functions の EventHub トリガー接続(手順3)とアプリ再起動を確認。App Insights の例外を確認。
- **SQL 接続失敗**: ファイアウォール(Azure許可 + 自IP)、`SqlConnectionString` の KV 参照値を確認。
- **配信が届かない**: `dbo.destination.is_active=1` と URL、`WebhookUrlGuard`(本番設定では http/プライベートは拒否)を確認。
