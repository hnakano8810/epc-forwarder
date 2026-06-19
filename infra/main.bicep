// EPC Forwarder インフラ(④)。IoT Hub / Flex Consumption Functions / Azure SQL / Key Vault / Storage / App Insights。
// 認証は MI 中心: Functions MI に Key Vault Secrets User と Storage Blob Data Owner を付与。
// SQL/IoT Hub の接続文字列は Key Vault に格納し、アプリ設定は KV 参照で注入。
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

// モジュールと同じ命名式で事前計算 — BCP120 回避のため existing/scope に使用
var kvName = toLower('${namePrefix}kv${suffix}')
var stName = toLower('${namePrefix}st${suffix}')

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

// Key Vault シークレット — parent に existing を使わず フルパス名で宣言 (BCP120 回避)
resource sqlConnSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  name: '${kvName}/SqlConnectionString'
  properties: {
    value: 'Server=tcp:${sql.outputs.sqlServerFqdn},1433;Database=${sql.outputs.databaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  }
  dependsOn: [keyvault]
}

resource iotConnSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  name: '${kvName}/IoTHubEventHubConnection'
  properties: { value: 'PLACEHOLDER_SET_BY_RUNBOOK' }
  dependsOn: [keyvault]
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
    sqlConnSecretUri: '${keyvault.outputs.keyVaultUri}secrets/SqlConnectionString'
    iotConnSecretUri: '${keyvault.outputs.keyVaultUri}secrets/IoTHubEventHubConnection'
    eventHubName: iothub.outputs.eventHubCompatiblePath
  }
}

// ロール割当 — scope に resourceGroup() を使用し、name は静的な値のみで guid を生成 (BCP120 回避)
// functionPrincipalId はデプロイ後に確定するため name には含められないので
// リソース ID + ロール ID の組み合わせで一意性を担保する
resource raKvSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: resourceGroup()
  name: guid(resourceId('Microsoft.KeyVault/vaults', kvName), roleKeyVaultSecretsUser, functions.name)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleKeyVaultSecretsUser)
    principalId: functions.outputs.functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource raStorageBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: resourceGroup()
  name: guid(resourceId('Microsoft.Storage/storageAccounts', stName), roleStorageBlobDataOwner, functions.name)
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
