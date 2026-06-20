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
    // 重要: ARM/Bicep で IoT Hub を作るとフォールバックルートは既定で「無効」になる
    // (ポータル作成時は有効)。明示的に有効化しないと D2C テレメトリが組込み events
    // エンドポイントに届かず、EventHub トリガー(取込)が空読みになる。
    routing: {
      fallbackRoute: {
        name: '$fallback'
        source: 'DeviceMessages'
        condition: 'true'
        endpointNames: [ 'events' ]
        isEnabled: true
      }
    }
    minTlsVersion: '1.2'
  }
}

// 取込関数専用の consumer group($Default を監視ツール等と取り合わないため)。
// IngestionFunction の EventHubTrigger が ConsumerGroup="functions" を参照する。
resource ingestionConsumerGroup 'Microsoft.Devices/IotHubs/eventHubEndpoints/ConsumerGroups@2023-06-30' = {
  name: '${iotHub.name}/events/functions'
  properties: {
    name: 'functions'
  }
}

output iotHubName string = iotHub.name
output eventHubCompatiblePath string = iotHub.properties.eventHubEndpoints.events.path
output eventHubCompatibleEndpoint string = iotHub.properties.eventHubEndpoints.events.endpoint
output iotHubId string = iotHub.id
