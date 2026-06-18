// EPC Forwarder インフラ定義（プレースホルダ）
// 詳細は docs/design および基本設計 §4/§8 を参照。
// 構成予定: IoT Hub / Azure Functions(Consumption) / Azure SQL / Key Vault / Storage / App Insights
targetScope = 'resourceGroup'

@description('リソース名のプレフィックス')
param namePrefix string = 'epcf'

@description('デプロイ先リージョン')
param location string = resourceGroup().location

// TODO: modules/ に各リソースを定義して参照する
