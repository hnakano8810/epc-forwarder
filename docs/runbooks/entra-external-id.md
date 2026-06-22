# Entra External ID マルチテナント認証 構築手順

HTTP API は External ID(CIAM)が発行する JWT を検証する。実テナント構築は手動。

## 1. External ID テナント作成
Azure ポータル → Microsoft Entra External ID → 外部テナントを作成(CIAM)。

## 2. API アプリ登録
- アプリの登録 → 新規登録(名: epcf-api)。
- 「アプリケーション ID の URI」を設定(例: `api://epcf-api`)= これが **audience**。

## 3. カスタムユーザー属性
- ユーザー属性 → カスタム属性 `tenantId`(String)を作成。
- トークンに出すと `extension_<appid>_tenantId` というクレーム名になる(この実値を控える)。

## 4. ユーザーフロー
- サインアップ&サインインのユーザーフローを作成し、アプリに紐づけ。
- 属性 `tenantId` を「アプリケーションクレーム」に含める。

## 5. テストユーザー
- ユーザーを作成し、`tenantId = <SQL の dbo.tenant.code>`(例: `acme`)を設定。

## 6. メタデータ確認
- OIDC メタデータ: `https://<tenant>.ciamlogin.com/<tenant-id>/v2.0/.well-known/openid-configuration`
- issuer はメタデータの `issuer` 値。

## 7. Functions アプリ設定
以下を投入(環境変数は二重アンダースコア):
- `Auth__Issuer` = メタデータの issuer
- `Auth__Audience` = `api://epcf-api`
- `Auth__TenantClaim` = `extension_<appid>_tenantId`(手順3の実クレーム名)
- `Auth__MetadataAddress` = 手順6の URL

設定後 `az functionapp restart`。

## 8. 動作確認
External ID からアクセストークンを取得し:
```bash
curl -H "Authorization: Bearer <token>" https://<func>.azurewebsites.net/api/sessions/<sid>/summary
```
- トークン無し → 401、tenant 不明 → 403、別テナントのセッション → 404、正常 → 200。

> deploy.sh / bicep への Auth__* 投入自動化は将来対応(本実装はアプリ設定を読むのみ)。
