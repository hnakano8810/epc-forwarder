# ④ Bicep インフラ ＋ 実機デプロイ手順書 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** EPC Forwarder の Azure 一式(IoT Hub / Flex Consumption Functions / Azure SQL / Key Vault / Storage / Application Insights)を Bicep で定義し、ユーザーが `!` で実行できる az CLI デプロイ手順書＋実機E2E手順を整備する。

**Architecture:** `infra/modules/*.bicep` に各リソースを分割し `infra/main.bicep` で合成。認証は MI 中心の現実解 — Functions のシステム割当 MI に Key Vault Secrets User / Storage Blob Data Owner を付与。SQL と IoT Hub(EventHub互換エンドポイント)の接続文字列は Key Vault シークレットに格納し、アプリ設定は Key Vault 参照(`@Microsoft.KeyVault(...)`)で注入(平文シークレットをアプリ設定に置かない)。デプロイは `az deployment group create` ＋ `func azure functionapp publish` ＋ sqlcmd(マイグレーション)の手順書。

**Tech Stack:** Bicep(`az bicep` 0.38.x で `build`/`lint` 検証), Azure CLI 2.81, Azure Functions Flex Consumption(.NET 8 isolated), Azure SQL(サーバーレス), Key Vault(RBAC), IoT Hub(S1)。

**検証方針(重要):**
- 私(実装者)は Azure にデプロイ不可。**Bicep の検証は `az bicep build --file <module>`(コンパイル＋lint、警告ゼロ)まで**。デプロイ時の意味的正しさ(ロール伝播・KV参照解決・Flex Consumption の設定形)は **ユーザーが実機デプロイで確認**する前提で、手順書に検証ステップを各所へ入れる。
- Bicep のタスクは「`az bicep build` が 0 エラー・0 警告」が緑判定。実 .NET ソリューションのビルド/テストには影響しない(infra は別ツリー)。
- 手順書(Markdown)はユーザーが `!` で実行。各コマンドは冪等性・検証出力を意識して並べる。

**前提コマンド:**
```bash
# az / bicep はインストール済(az 2.81, az bicep 0.38.3)。dotnet は ④ では不要だが publish 検証で使う場合:
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
```

**決定事項(2026-06-19):** Functions=Flex Consumption / 認証=MI中心(KV・IoT Hub は MI、SQL は SQL認証接続文字列を Key Vault 格納) / デプロイ=az CLI 手順書。

---

## 既存資産(grounding)
- `infra/main.bicep`(プレースホルダ: `namePrefix`/`location` パラメタのみ)、`infra/modules/.gitkeep`。
- マイグレーション `db/migrations/0001_initial.sql`, `0002_destinations.sql`(冪等な `IF OBJECT_ID(...) IS NULL` / `IF NOT EXISTS` ガード付き)。
- アプリが読む構成キー(Functions `Program.cs`): `SqlConnectionString`(必須), `KeyVaultUri`(任意, per-destination の HMAC/ヘッダシークレット解決用)。EventHub トリガー: `Connection="IoTHubEventHubConnection"`, `eventHubName="%IoTHubEventHubName%"`。`AzureWebJobsStorage`, `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`, `APPLICATIONINSIGHTS_CONNECTION_STRING`(OpenTelemetry 結線は接続文字列があれば有効)。
- 宛先設定: `dbo.destination`/`destination_header`/`destination_mask`(0002)。HMAC/認証ヘッダ値は Key Vault(参照名のみDB)。

---

## File Structure
- Create `infra/modules/monitoring.bicep` — Log Analytics + Application Insights。
- Create `infra/modules/storage.bicep` — Storage(Functions ランタイム＋デプロイ用 blob コンテナ)。
- Create `infra/modules/keyvault.bicep` — Key Vault(RBAC)＋シークレット枠。
- Create `infra/modules/sql.bicep` — Azure SQL サーバー＋サーバーレスDB＋ファイアウォール。
- Create `infra/modules/iothub.bicep` — IoT Hub(S1)。
- Create `infra/modules/functions.bicep` — Flex Consumption プラン＋Function App(MI＋アプリ設定＋KV参照)。
- Rewrite `infra/main.bicep` — 全モジュール合成＋ロール割当＋KVシークレット投入＋outputs。
- Create `infra/main.bicepparam` — パラメタ既定。
- Create `infra/README.md` — 構成概要。
- Create `docs/runbooks/deploy.md` — デプロイ＋実機E2E手順書。

> 各 Bicep の API バージョンは実装時に `az bicep build` が解決できる安定版を使用。本計画のコードは 2024 年系の安定 API を用いる。`az bicep build` が未知 API/型で警告/エラーを出したら、その指摘に従い同等の安定版へ調整すること(=計画の意図を保ったまま az の検証を緑にする)。

---

## Task 1: monitoring モジュール(Log Analytics + App Insights)

**Files:**
- Create: `infra/modules/monitoring.bicep`

- [ ] **Step 1: モジュールを作成**

`infra/modules/monitoring.bicep`:

```bicep
@description('リソース名プレフィックス')
param namePrefix string
@description('リージョン')
param location string

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-law'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
  }
}

output appInsightsConnectionString string = appi.properties.ConnectionString
output lawId string = law.id
```

- [ ] **Step 2: Bicep を検証**

Run: `az bicep build --file infra/modules/monitoring.bicep --stdout > /dev/null && echo OK`
Expected: `OK`(0 エラー・0 警告。警告が出たら API/プロパティを az の指摘どおり調整)。

- [ ] **Step 3: コミット**

```bash
git add infra/modules/monitoring.bicep
git commit -m "feat(infra): monitoring モジュール(Log Analytics + App Insights)"
```

---

## Task 2: storage モジュール

Flex Consumption は (a) ランタイムの `AzureWebJobsStorage`(MI 接続) と (b) デプロイパッケージ用 blob コンテナ を必要とする。

**Files:**
- Create: `infra/modules/storage.bicep`

- [ ] **Step 1: モジュールを作成**

`infra/modules/storage.bicep`:

```bicep
@description('リソース名プレフィックス')
param namePrefix string
@description('リージョン')
param location string
@description('グローバル一意サフィックス')
param suffix string

@description('デプロイパッケージ用 blob コンテナ名')
var deployContainerName = 'deploy'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: toLower('${namePrefix}st${suffix}')
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deployContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deployContainerName
  properties: { publicAccess: 'None' }
}

output storageAccountName string = storage.name
output storageBlobEndpoint string = storage.properties.primaryEndpoints.blob
output deployContainerName string = deployContainerName
output storageId string = storage.id
```

- [ ] **Step 2: 検証**

Run: `az bicep build --file infra/modules/storage.bicep --stdout > /dev/null && echo OK`
Expected: `OK`。

- [ ] **Step 3: コミット**

```bash
git add infra/modules/storage.bicep
git commit -m "feat(infra): storage モジュール(Functions ランタイム＋デプロイコンテナ)"
```

---

## Task 3: keyvault モジュール

RBAC モードの Key Vault。SQL/IoT Hub 接続文字列のシークレットは main.bicep 側で投入する(値が他リソース由来のため)。本モジュールは Vault と参照可能な情報のみを返す。

**Files:**
- Create: `infra/modules/keyvault.bicep`

- [ ] **Step 1: モジュールを作成**

`infra/modules/keyvault.bicep`:

```bicep
@description('リソース名プレフィックス')
param namePrefix string
@description('リージョン')
param location string
@description('グローバル一意サフィックス')
param suffix string

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: toLower('${namePrefix}kv${suffix}')
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

output keyVaultName string = kv.name
output keyVaultUri string = kv.properties.vaultUri
output keyVaultId string = kv.id
```

- [ ] **Step 2: 検証**

Run: `az bicep build --file infra/modules/keyvault.bicep --stdout > /dev/null && echo OK`
Expected: `OK`。

- [ ] **Step 3: コミット**

```bash
git add infra/modules/keyvault.bicep
git commit -m "feat(infra): keyvault モジュール(RBAC)"
```

---

## Task 4: sql モジュール(サーバー＋サーバーレスDB)

**Files:**
- Create: `infra/modules/sql.bicep`

- [ ] **Step 1: モジュールを作成**

`infra/modules/sql.bicep`:

```bicep
@description('リソース名プレフィックス')
param namePrefix string
@description('リージョン')
param location string
@description('グローバル一意サフィックス')
param suffix string
@description('SQL 管理者ログイン名')
param sqlAdminLogin string
@description('SQL 管理者パスワード')
@secure()
param sqlAdminPassword string
@description('データベース名')
param databaseName string = 'epcf'

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: toLower('${namePrefix}sql${suffix}')
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// サーバーレス General Purpose(自動一時停止つき)。
resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: { name: 'GP_S_Gen5_1', tier: 'GeneralPurpose', family: 'Gen5', capacity: 1 }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
  }
}

// Azure サービス(Functions 等)からの接続を許可(0.0.0.0)。PoC 用途。
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = databaseName
```

- [ ] **Step 2: 検証**

Run: `az bicep build --file infra/modules/sql.bicep --stdout > /dev/null && echo OK`
Expected: `OK`。

- [ ] **Step 3: コミット**

```bash
git add infra/modules/sql.bicep
git commit -m "feat(infra): sql モジュール(サーバー＋サーバーレスDB＋FW)"
```

---

## Task 5: iothub モジュール

**Files:**
- Create: `infra/modules/iothub.bicep`

- [ ] **Step 1: モジュールを作成**

`infra/modules/iothub.bicep`:

```bicep
@description('リソース名プレフィックス')
param namePrefix string
@description('リージョン')
param location string
@description('グローバル一意サフィックス')
param suffix string

resource iotHub 'Microsoft.Devices/IotHubs@2023-06-30' = {
  name: toLower('${namePrefix}-iot-${suffix}')
  location: location
  sku: { name: 'S1', capacity: 1 }
  properties: {
    // 既定の組込み Event Hubs 互換エンドポイント(events)を Functions が購読する。
    eventHubEndpoints: {
      events: { retentionTimeInDays: 1, partitionCount: 4 }
    }
    minTlsVersion: '1.2'
  }
}

output iotHubName string = iotHub.name
// EventHub 互換エンドポイントのエンティティパス(トリガーの eventHubName に使う)。
output eventHubCompatiblePath string = iotHub.properties.eventHubEndpoints.events.path
output eventHubCompatibleEndpoint string = iotHub.properties.eventHubEndpoints.events.endpoint
output iotHubId string = iotHub.id
```

- [ ] **Step 2: 検証**

Run: `az bicep build --file infra/modules/iothub.bicep --stdout > /dev/null && echo OK`
Expected: `OK`。`eventHubEndpoints.events.path`/`.endpoint` プロパティが未知と警告された場合は、`az bicep build` の指摘に従い、出力を `iotHub.properties.eventHubEndpoints.events.path` のままにしつつ(実デプロイで解決される実行時プロパティ)、型警告が出るなら該当 output を手順書側で `az iot hub` から取得する方式へ寄せる旨をコメントで明記。

- [ ] **Step 3: コミット**

```bash
git add infra/modules/iothub.bicep
git commit -m "feat(infra): iothub モジュール(S1 + 組込みEHエンドポイント)"
```

---

## Task 6: functions モジュール(Flex Consumption + MI + KV参照)

**Files:**
- Create: `infra/modules/functions.bicep`

- [ ] **Step 1: モジュールを作成**

`infra/modules/functions.bicep`:

```bicep
@description('リソース名プレフィックス')
param namePrefix string
@description('リージョン')
param location string
@description('グローバル一意サフィックス')
param suffix string

@description('デプロイパッケージ用ストレージ blob エンドポイント')
param storageBlobEndpoint string
@description('デプロイコンテナ名')
param deployContainerName string
@description('ランタイム用ストレージアカウント名(AzureWebJobsStorage__accountName)')
param storageAccountName string
@description('App Insights 接続文字列')
param appInsightsConnectionString string
@description('Key Vault URI(アプリの per-destination シークレット解決用)')
param keyVaultUri string
@description('SqlConnectionString シークレットの URI(KV参照)')
param sqlConnSecretUri string
@description('IoTHubEventHubConnection シークレットの URI(KV参照)')
param iotConnSecretUri string
@description('EventHub 互換エンドポイントのエンティティパス')
param eventHubName string

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${namePrefix}-plan'
  location: location
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  kind: 'functionapp'
  properties: { reserved: true }
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: toLower('${namePrefix}-func-${suffix}')
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageBlobEndpoint}${deployContainerName}'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      runtime: { name: 'dotnet-isolated', version: '8.0' }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 2048 }
    }
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storageAccountName }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'KeyVaultUri', value: keyVaultUri }
        { name: 'SqlConnectionString', value: '@Microsoft.KeyVault(SecretUri=${sqlConnSecretUri})' }
        { name: 'IoTHubEventHubConnection', value: '@Microsoft.KeyVault(SecretUri=${iotConnSecretUri})' }
        { name: 'IoTHubEventHubName', value: eventHubName }
      ]
    }
  }
}

output functionAppName string = site.name
output functionPrincipalId string = site.identity.principalId
```

NOTE(実装者向け): Flex Consumption の Bicep 形(`functionAppConfig` / `FC1` / `2023-12-01`)は `az bicep build` で型解決される。もし API バージョンや `functionAppConfig` 配下プロパティで警告/エラーが出たら、`az bicep build` の指摘に厳密に従い、Flex Consumption の最新安定スキーマへ寄せる(プラン名 `FC1`/tier `FlexConsumption`、`runtime.name='dotnet-isolated'`、`deployment.storage.authentication.type='SystemAssignedIdentity'` の意図は維持)。`AzureWebJobsStorage__accountName`(MI接続)を使うため、main.bicep で Functions MI に Storage Blob Data Owner を割当(Task 7)。

- [ ] **Step 2: 検証**

Run: `az bicep build --file infra/modules/functions.bicep --stdout > /dev/null && echo OK`
Expected: `OK`(警告ゼロ)。警告は az の指摘どおり解消。

- [ ] **Step 3: コミット**

```bash
git add infra/modules/functions.bicep
git commit -m "feat(infra): functions モジュール(Flex Consumption + MI + KV参照)"
```

---

## Task 7: main.bicep 合成(モジュール＋ロール割当＋KVシークレット＋outputs)

**Files:**
- Rewrite: `infra/main.bicep`

- [ ] **Step 1: main.bicep を全面書き換え**

`infra/main.bicep`:

```bicep
// EPC Forwarder インフラ(④)。IoT Hub / Flex Consumption Functions / Azure SQL / Key Vault / Storage / App Insights。
// 認証は MI 中心: Functions MI に Key Vault Secrets User と Storage Blob Data Owner を付与。
// SQL/IoT Hub の接続文字列は Key Vault に格納し、アプリ設定は KV 参照で注入。
targetScope = 'resourceGroup'

@description('リソース名プレフィックス')
param namePrefix string = 'epcf'
@description('デプロイ先リージョン')
param location string = resourceGroup().location
@description('SQL 管理者ログイン名')
param sqlAdminLogin string
@description('SQL 管理者パスワード')
@secure()
param sqlAdminPassword string

var suffix = uniqueString(resourceGroup().id)

// 組込みロール定義 ID
var roleKeyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var roleStorageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: { namePrefix: namePrefix, location: location }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: { namePrefix: namePrefix, location: location, suffix: suffix }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: { namePrefix: namePrefix, location: location, suffix: suffix }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    namePrefix: namePrefix
    location: location
    suffix: suffix
    sqlAdminLogin: sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
  }
}

module iothub 'modules/iothub.bicep' = {
  name: 'iothub'
  params: { namePrefix: namePrefix, location: location, suffix: suffix }
}

// 既存 Key Vault を参照(シークレット投入用)
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyvault.outputs.keyVaultName
}

// SQL 接続文字列(SQL 認証)を KV シークレットへ。
resource sqlConnSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'SqlConnectionString'
  properties: {
    value: 'Server=tcp:${sql.outputs.sqlServerFqdn},1433;Database=${sql.outputs.databaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  }
}

// IoT Hub の EventHub 互換接続文字列はデプロイ後に手順書で投入する(listKeys 由来)。
// ここでは空のプレースホルダ・シークレットを作り、Functions のアプリ設定が KV 参照できるようにする。
resource iotConnSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'IoTHubEventHubConnection'
  properties: { value: 'PLACEHOLDER_SET_BY_RUNBOOK' }
}

module functions 'modules/functions.bicep' = {
  name: 'functions'
  params: {
    namePrefix: namePrefix
    location: location
    suffix: suffix
    storageBlobEndpoint: storage.outputs.storageBlobEndpoint
    deployContainerName: storage.outputs.deployContainerName
    storageAccountName: storage.outputs.storageAccountName
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    keyVaultUri: keyvault.outputs.keyVaultUri
    sqlConnSecretUri: sqlConnSecret.properties.secretUri
    iotConnSecretUri: iotConnSecret.properties.secretUri
    eventHubName: iothub.outputs.eventHubCompatiblePath
  }
}

// Functions MI → Key Vault Secrets User(KV参照解決＋アプリの per-destination シークレット読取)
resource raKvSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(kv.id, functions.outputs.functionPrincipalId, roleKeyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleKeyVaultSecretsUser)
    principalId: functions.outputs.functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Functions MI → Storage Blob Data Owner(AzureWebJobsStorage(MI)＋デプロイコンテナ)
resource storageAcct 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storage.outputs.storageAccountName
}

resource raStorageBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAcct
  name: guid(storageAcct.id, functions.outputs.functionPrincipalId, roleStorageBlobDataOwner)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleStorageBlobDataOwner)
    principalId: functions.outputs.functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output resourceGroupName string = resourceGroup().name
output functionAppName string = functions.outputs.functionAppName
output functionPrincipalId string = functions.outputs.functionPrincipalId
output keyVaultName string = keyvault.outputs.keyVaultName
output sqlServerFqdn string = sql.outputs.sqlServerFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output iotHubName string = iothub.outputs.iotHubName
output storageAccountName string = storage.outputs.storageAccountName
```

NOTE(実装者向け): `sqlConnSecret.properties.secretUri` / `iotConnSecret.properties.secretUri` が `az bicep build` で解決できない場合は、`'${kv.properties.vaultUri}secrets/SqlConnectionString'` 形式で URI を組み立てる方式に切り替える(意図: Functions の `@Microsoft.KeyVault(SecretUri=...)` に渡せる完全 URI)。ロール定義 GUID は不変の組込みロール ID(Key Vault Secrets User / Storage Blob Data Owner)。

- [ ] **Step 2: 検証**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK`
Expected: `OK`(0 エラー・0 警告)。警告は az の指摘どおり解消(意図は維持)。

- [ ] **Step 3: コミット**

```bash
git add infra/main.bicep
git commit -m "feat(infra): main.bicep 合成(モジュール＋ロール割当＋KVシークレット)"
```

---

## Task 8: パラメタファイル ＋ infra README

**Files:**
- Create: `infra/main.bicepparam`
- Create: `infra/README.md`

- [ ] **Step 1: パラメタファイルを作成**

`infra/main.bicepparam`:

```bicep
using './main.bicep'

param namePrefix = 'epcf'
// location は未指定なら resourceGroup().location。必要なら明示:
// param location = 'japaneast'
param sqlAdminLogin = 'epcfadmin'
// パスワードはコミットしない。デプロイ時に -p sqlAdminPassword=... で上書きする(下記 README/手順書参照)。
param sqlAdminPassword = ''
```

- [ ] **Step 2: infra README を作成**

`infra/README.md`:

````markdown
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
- Functions のシステム割当 MI に **Key Vault Secrets User** と **Storage Blob Data Owner** を付与。
- `SqlConnectionString`(SQL認証)と `IoTHubEventHubConnection`(EH互換)は **Key Vault** に格納し、アプリ設定は `@Microsoft.KeyVault(SecretUri=...)` 参照で注入(平文をアプリ設定に置かない)。
- IoT Hub の EH 互換接続文字列は **デプロイ後に手順書で KV へ投入**(listKeys 由来のため)。

## ローカル検証
```bash
az bicep build --file infra/main.bicep --stdout > /dev/null && echo OK
```
> 本リポジトリの作成者は Azure へデプロイしない。Bicep は `az bicep build`(コンパイル＋lint)まで検証済。実デプロイ時の検証は手順書に従う。
````

- [ ] **Step 3: 検証(bicepparam も build できる)**

Run: `az bicep build-params --file infra/main.bicepparam --stdout > /dev/null && echo OK`
Expected: `OK`。

- [ ] **Step 4: コミット**

```bash
git add infra/main.bicepparam infra/README.md
git commit -m "feat(infra): main.bicepparam と infra/README"
```

---

## Task 9: デプロイ＋実機E2E 手順書

**Files:**
- Create: `docs/runbooks/deploy.md`

- [ ] **Step 1: 手順書を作成**

`docs/runbooks/deploy.md`:

````markdown
# EPC Forwarder デプロイ＋実機E2E 手順書

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

## 3. IoT Hub の EventHub 互換接続文字列を Key Vault へ投入
```bash
# 組込みエンドポイント(events)の EH 互換接続文字列を取得
IOT_EH_CONN=$(az iot hub connection-string show --hub-name "$IOT" --default-eventhub -o tsv)
# Key Vault のプレースホルダ・シークレットを実値で上書き
az keyvault secret set --vault-name "$KV" --name IoTHubEventHubConnection --value "$IOT_EH_CONN" -o none
echo "IoTHubEventHubConnection set."
```
> Functions アプリ設定 `IoTHubEventHubConnection` は KV 参照で本シークレットを指す。設定後、アプリ再起動で反映:
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
(`tenant` `product` `session` `reading` `snapshot` `destination` `destination_header` `destination_mask` が並ぶ)

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
- **配信が届かない**: `dbo.destination.active=1` と URL、`WebhookUrlGuard`(本番設定では http/プライベートは拒否)を確認。
````

- [ ] **Step 2: リンク健全性の確認(参照ファイルが存在)**

Run:
```bash
ls docs/design/ingestion-contract.md db/migrations/0001_initial.sql db/migrations/0002_destinations.sql infra/main.bicep && echo OK
```
Expected: `OK`。

- [ ] **Step 3: コミット**

```bash
git add docs/runbooks/deploy.md
git commit -m "docs: デプロイ＋実機E2E 手順書(runbook)"
```

---

## Task 10: 全体検証

- [ ] **Step 1: 全 Bicep を再ビルド(0 エラー・0 警告)**

Run:
```bash
for f in infra/modules/*.bicep infra/main.bicep; do echo "== $f =="; az bicep build --file "$f" --stdout > /dev/null && echo OK || echo "FAILED: $f"; done
az bicep build-params --file infra/main.bicepparam --stdout > /dev/null && echo "params OK"
```
Expected: 各 `OK` ＋ `params OK`。**実際の出力を貼ること。** 失敗があれば該当モジュールを az の指摘どおり修正(意図維持)。

- [ ] **Step 2: .NET ソリューションに影響がないことを確認**

Run: `export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH && dotnet build EpcForwarder.sln`
Expected: 0 警告・成功(infra 追加はコードに非影響)。

- [ ] **Step 3: 最終コミット(あれば)＆完了**

```bash
git status --porcelain
```
(未コミットがあればまとめてコミット)

---

## Self-Review チェック結果
- **スコープ網羅**: IoT Hub / Flex Consumption Functions / Azure SQL(サーバーレス)/ Key Vault / Storage / App Insights を Bicep 化(Task 1-7)、パラメタ＋README(Task 8)、デプロイ＋実機E2E手順書(Task 9: 取込→自動配信、クエリAPI、棚卸 provisional/finalize、他テナント404)、全体検証(Task 10)。決定事項(Flex Consumption / MI中心 / az CLI)を反映。
- **認証の一貫性**: Functions MI → KV Secrets User / Storage Blob Data Owner。SQL/IoT Hub 接続文字列は KV 格納＋アプリ設定は KV 参照。アプリの `KeyVaultUri`/`SqlConnectionString`/`IoTHubEventHubConnection`/`IoTHubEventHubName` は Program.cs/トリガーの実キーと一致。
- **検証可能性**: 各 Bicep タスクは `az bicep build`(コンパイル＋lint)で緑判定。デプロイ意味論はユーザー実機(手順書に検証ステップ)。`az bicep build` が API/プロパティ警告を出した場合は意図維持で az の指摘どおり調整、と各タスクに明記(プレースホルダ回避のための既知の調整指示であり、未確定コードではない)。
- **プレースホルダ**: Bicep/手順書とも実コードを記載。KV の IoT 接続文字列のみ「デプロイ後に手順書で投入」する設計(値が listKeys 由来のため)で、Bicep は参照可能な空シークレットを用意=意図的。
- **既知の前提/リスク**: Flex Consumption と IoT Hub の Bicep 形は az の型解決に従って微調整しうる(各 NOTE 参照)。手順書の SQL シード列は実マイグレーション定義に合わせる指示を明記。
