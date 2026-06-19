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

resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = databaseName
