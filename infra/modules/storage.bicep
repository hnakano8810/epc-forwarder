@description('リソース名プレフィックス')
@minLength(1)
param namePrefix string
@description('リージョン')
param location string
@description('グローバル一意サフィックス')
param suffix string

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
