# infra — EPC Forwarder Bicep

Azure 一式を Bicep で定義する。詳細なデプロイ＋実機E2E手順は `docs/runbooks/deploy.md`。

## 構成
| モジュール | リソース |
|---|---|
| monitoring | Log Analytics + Application Insights |
| storage | Storage(Functions ランタイム＋デプロイ blob コンテナ) |
| keyvault | Key Vault(RBAC) |
| sql | Azure SQL サーバー＋サーバーレスDB(GP_S_Gen5_1)＋FW(Azure許可) |
| iothub | IoT Hub S1(組込み EH 互換エンドポイント) |
| functions | Flex Consumption(FC1) + .NET8 isolated + システム割当MI |

## 認証(MI中心)
- Functions のシステム割当 MI に **Key Vault Secrets User**(Vaultスコープ)と **Storage Blob Data Owner**(ストレージアカウントスコープ)を付与。
- `SqlConnectionString`(SQL認証)と `IoTHubEventHubConnection`(EH互換)は **Key Vault** に格納し、アプリ設定は `@Microsoft.KeyVault(SecretUri=...)` 参照で注入(平文をアプリ設定に置かない)。
- IoT Hub の EH 互換接続文字列は **デプロイ後に手順書で KV へ投入**(listKeys 由来のため。Bicep はプレースホルダ・シークレットを用意)。

## ローカル検証
```bash
az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK
az bicep build-params --file infra/main.bicepparam --stdout > /dev/null && echo "params OK"
```
> 本リポジトリの作成者は Azure へデプロイしない。Bicep は `az bicep build`(コンパイル＋lint)まで検証済。実デプロイ時の検証は手順書に従う。
