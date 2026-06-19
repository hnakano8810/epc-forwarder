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
    eventHubEndpoints: {
      events: { retentionTimeInDays: 1, partitionCount: 4 }
    }
    minTlsVersion: '1.2'
  }
}

output iotHubName string = iotHub.name
output eventHubCompatiblePath string = iotHub.properties.eventHubEndpoints.events.path
output eventHubCompatibleEndpoint string = iotHub.properties.eventHubEndpoints.events.endpoint
output iotHubId string = iotHub.id
