#!/usr/bin/env bash
# scripts/deploy.sh — EPC Forwarder インフラ(Azureリソース)プロビジョニング。再現性・再実行安全。
# 役割分担: 本スクリプトは「リソース作成 + リソース間結線」まで。
#   アプリ更新(migrate / publish)と E2E は GitHub Actions(main マージ)で実行する
#   (.github/workflows/ci-cd.yml)。詳細は docs/runbooks/deploy.md。
# 前提: bash, az(+az bicep, az iot)。az login 済み。値は scripts/deploy.env から読む。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CURRENT_STEP="init"
trap 'echo ">>> FAILED at step: $CURRENT_STEP" >&2' ERR

log() { echo "=== $* ==="; }

# --- 0. preflight ---
preflight() {
  CURRENT_STEP="preflight"
  command -v az >/dev/null 2>&1 || { echo "ERROR: 'az' が見つかりません。" >&2; exit 1; }
  if [ ! -f "$SCRIPT_DIR/deploy.env" ]; then
    echo "ERROR: $SCRIPT_DIR/deploy.env がありません。deploy.env.example をコピーして値を埋めてください。" >&2
    exit 1
  fi
  # shellcheck disable=SC1091
  set -a; . "$SCRIPT_DIR/deploy.env"; set +a
  : "${SUBSCRIPTION_ID:?deploy.env に SUBSCRIPTION_ID が必要}"
  : "${RG:?}" "${LOCATION:?}" "${PREFIX:?}" "${SQL_ADMIN:?}" "${SQL_PASSWORD:?}"
  if ! az account show >/dev/null 2>&1; then
    echo "ERROR: 未ログインです。先に 'az login'(必要なら --tenant 指定)を実行してください。" >&2
    exit 1
  fi
  az account set --subscription "$SUBSCRIPTION_ID"
  log "preflight OK (sub=$SUBSCRIPTION_ID rg=$RG)"
}

# --- 1. リソースグループ ---
ensure_rg() {
  CURRENT_STEP="resource-group"
  az group create -n "$RG" -l "$LOCATION" -o none
  log "resource group ready"
}

# --- 2. Bicep デプロイ + 出力取得 ---
# Auth__*(External ID)も bicep パラメータで投入される。テナント未作成のうちは空のまま
# (= 認証ミドルウェアは fail-closed)。作成後 deploy.env の AUTH_* を埋めて再実行する。
deploy_bicep() {
  CURRENT_STEP="bicep"
  set +x
  az deployment group create -g "$RG" -n main \
    -f "$REPO_ROOT/infra/main.bicep" \
    -p namePrefix="$PREFIX" sqlAdminLogin="$SQL_ADMIN" sqlAdminPassword="$SQL_PASSWORD" \
       authIssuer="${AUTH_ISSUER:-}" authAudience="${AUTH_AUDIENCE:-}" \
       authTenantClaim="${AUTH_TENANT_CLAIM:-}" authMetadataAddress="${AUTH_METADATA_ADDRESS:-}" \
    -o none
  FUNC=$(az deployment group show -g "$RG" -n main --query properties.outputs.functionAppName.value -o tsv | tr -d '\r\n')
  IOT=$(az deployment group show -g "$RG" -n main --query properties.outputs.iotHubName.value -o tsv | tr -d '\r\n')
  log "bicep deployed: FUNC=$FUNC IOT=$IOT"
}

# --- 3. IoT 接続文字列を app 設定へ ---
# (実行時 listKeys 相当が要るため bicep でなくスクリプトで結線。publish は app 設定を消さない)
set_iot_connection() {
  CURRENT_STEP="iot-connection"
  local conn
  conn=$(az iot hub connection-string show -g "$RG" --hub-name "$IOT" --default-eventhub -o tsv | tr -d '\r\n')
  az functionapp config appsettings set -g "$RG" -n "$FUNC" \
    --settings "IoTHubEventHubConnection=$conn" -o none
  az functionapp restart -g "$RG" -n "$FUNC" -o none
  log "IoT connection set on $FUNC"
}

main() {
  preflight
  ensure_rg
  deploy_bicep
  set_iot_connection
  log "INFRA DONE. FUNC=$FUNC IOT=$IOT  (アプリ更新/E2E は CI=main マージで実行)"
}

main "$@"
