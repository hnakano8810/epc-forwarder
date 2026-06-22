# EPC Forwarder デプロイ＋実機E2E 手順書

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
### 8.1 取込(read → complete)→ 自動配信(伝票)
`az iot device send-d2c-message` でデバイスメッセージ(取込契約 `docs/design/ingestion-contract.md`)を送る:
```bash
# read 1件(SKU解決される EPC)
az iot device send-d2c-message --hub-name "$IOT" --device-id handy-07 \
  --data '{"kind":"read","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001","business_key":"DN-1","session_type":"shipment","resolve_sku":true,"epc":"302DB42318A0038000001231","device_id":"handy-07","read_at":"2026-06-19T00:00:01Z"}'
# complete(expected=1)
az iot device send-d2c-message --hub-name "$IOT" --device-id handy-07 \
  --data '{"kind":"complete","tenant":1,"session_id":"9c3a8f10-0000-0000-0000-000000000001","expected_count":1}'
```
確認:
- webhook.site に確定スナップショット POST(`is_final:true`, `items:[{sku:ITEM-AAA,quantity:1}]`)が届く。
- App Insights のトレースに `Completion session=... delivered=True`。
```bash
az monitor app-insights query -g "$RG" --apps "${PREFIX}-appi" \
  --analytics-query "traces | where message contains 'Completion' | top 5 by timestamp desc" -o table || \
  echo "(App Insights クエリは数分の取込遅延あり)"
```

### 8.2 クエリAPI(端末プル)
```bash
SID=9c3a8f10-0000-0000-0000-000000000001
curl -s "https://$FUNC_HOST/api/sessions/$SID/summary?code=$FUNC_KEY" -H "X-EPCF-Tenant: 1" | jq .
curl -s "https://$FUNC_HOST/api/sessions/$SID/reconciliation?code=$FUNC_KEY" -H "X-EPCF-Tenant: 1" | jq .
curl -s "https://$FUNC_HOST/api/sessions/$SID/unknown?code=$FUNC_KEY" -H "X-EPCF-Tenant: 1" | jq .
# 他テナントは 404
curl -s -o /dev/null -w "%{http_code}\n" "https://$FUNC_HOST/api/sessions/$SID/summary?code=$FUNC_KEY" -H "X-EPCF-Tenant: 2"
```
期待: summary は `total_quantity:1` / `items:[{sku:ITEM-AAA,quantity:1}]` / `unknown_count:0`、reconciliation は `expected:1,received:1,match:true`、他テナントは `404`。

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
