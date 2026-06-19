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
