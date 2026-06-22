@description('リソース名プレフィックス')
@minLength(1)
param namePrefix string
@description('リージョン')
param location string
@description('グローバル一意サフィックス')
param suffix string

@description('AzureWebJobsStorage 用ストレージ接続文字列(Consumption はキー接続が必須)')
@secure()
param storageConnectionString string
@description('App Insights 接続文字列')
param appInsightsConnectionString string
@description('Key Vault URI(アプリの per-destination シークレット解決用)')
param keyVaultUri string
@description('SQL 接続文字列(リテラル注入)')
@secure()
param sqlConnectionString string
@description('IoT Hub EventHub 互換接続文字列(初期はプレースホルダ、デプロイ後CLIで設定)')
@secure()
param iotEventHubConnectionString string
@description('EventHub 互換エンティティパス')
param eventHubName string

// Linux Consumption(Y1)。Flex の functionAppConfig は使わず従来の siteConfig/appSettings。
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${namePrefix}-plan'
  location: location
  sku: { name: 'Y1', tier: 'Dynamic' }
  kind: 'functionapp'
  properties: { reserved: true } // Linux
}

resource site 'Microsoft.Web/sites@2023-12-01' = {
  name: toLower('${namePrefix}-func-${suffix}')
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' } // per-destination シークレットを KeyVaultSecretStore(DefaultAzureCredential)で解決
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: storageConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        { name: 'KeyVaultUri', value: keyVaultUri }
        { name: 'SqlConnectionString', value: sqlConnectionString }
        { name: 'IoTHubEventHubConnection', value: iotEventHubConnectionString }
        { name: 'IoTHubEventHubName', value: eventHubName }
      ]
    }
  }
}

output functionAppName string = site.name
output functionPrincipalId string = site.identity.principalId
