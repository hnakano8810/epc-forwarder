// EPC Forwarder インフラ(④)。IoT Hub / Functions(Linux Consumption Y1) / Azure SQL / Key Vault / Storage / App Insights。
// Functions MI に Key Vault Secrets User(per-destination シークレット解決用)を付与。
// Y1 は AzureWebJobsStorage にキー接続が必須。SQL はリテラル注入、IoT 接続はデプロイ後にCLIで設定。
targetScope = 'resourceGroup'

@description('リソース名プレフィックス')
@minLength(1)
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

// モジュールと同じ命名式で事前計算 — var 名由来の existing 参照は
// コンパイル時に解決可能で BCP120 を回避でき、scope/parent に使用できる
var kvName = toLower('${namePrefix}kv${suffix}')
var storageName = toLower('${namePrefix}st${suffix}')

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

// keyvault/storage モジュールが作成する実体を var 名で参照 (existing)。
// name が module 出力でなく var なので scope/parent に使っても BCP120 にならない。
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: kvName
}

resource storageAcct 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageName
}

// 接続文字列。Y1 は AzureWebJobsStorage にキー接続が必須。SQL はKV参照を避けリテラル注入。
var sqlConn = 'Server=tcp:${sql.outputs.sqlServerFqdn},1433;Database=${sql.outputs.databaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var storageConn = 'DefaultEndpointsProtocol=https;AccountName=${storageName};EndpointSuffix=${environment().suffixes.storage};AccountKey=${listKeys(storageAcct.id, '2023-05-01').keys[0].value}'

// Key Vault シークレット(per-destination の HMAC/ヘッダ値を運用で投入する箱として残置。アプリの接続文字列はアプリ設定にリテラル注入)
resource sqlConnSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'SqlConnectionString'
  properties: { value: sqlConn }
  dependsOn: [keyvault]
}

module functions 'modules/functions.bicep' = {
  name: 'functions'
  params: {
    namePrefix: namePrefix
    location: location
    suffix: suffix
    storageConnectionString: storageConn
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    keyVaultUri: keyvault.outputs.keyVaultUri
    sqlConnectionString: sqlConn
    iotEventHubConnectionString: 'PLACEHOLDER_SET_POST_DEPLOY'
    eventHubName: iothub.outputs.eventHubCompatiblePath
  }
}

// ロール割当 — 特定リソースにスコープ(最小権限)。
// kv.id/storageAcct.id は var 名由来 existing の resourceId でコンパイル時解決可。
// name は静的要素のみ(principalId=module 出力 は properties 内に限る)。
// principalId 参照により functions への依存は暗黙に確保されるため dependsOn は不要。
resource raKvSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(kv.id, roleKeyVaultSecretsUser, functions.name)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleKeyVaultSecretsUser)
    principalId: functions.outputs.functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource raStorageBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAcct
  name: guid(storageAcct.id, roleStorageBlobDataOwner, functions.name)
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
