@description('リソース名プレフィックス')
@minLength(1)
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
