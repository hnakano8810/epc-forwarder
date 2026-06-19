using './main.bicep'

param namePrefix = 'epcf'
// location は未指定なら resourceGroup().location。必要なら明示:
// param location = 'japaneast'
param sqlAdminLogin = 'epcfadmin'
// パスワードはコミットしない。デプロイ時に -p sqlAdminPassword=... で上書きする(手順書参照)。
param sqlAdminPassword = ''
