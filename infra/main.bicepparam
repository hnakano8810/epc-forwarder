using './main.bicep'

param namePrefix = 'epcf'
// location は未指定なら resourceGroup().location。必要なら明示:
// param location = 'japaneast'
param sqlAdminLogin = 'epcfadmin'
// パスワードはコミットしない。デプロイ時に -p sqlAdminPassword=... で上書きする(手順書参照)。
param sqlAdminPassword = ''

// Entra External ID。テナント未作成のうちは空(認証ミドルウェアは fail-closed)。
// 作成後 deploy.sh が deploy.env の AUTH_* を -p で上書きする(entra-external-id.md)。
param authIssuer = ''
param authAudience = ''
param authTenantClaim = ''
param authMetadataAddress = ''
