#!/usr/bin/env bash
# scripts/deploy.sh — EPC Forwarder 一括デプロイ(再現性・再実行安全)。
# 前提: bash, az(+az bicep, az iot), func v4, dotnet SDK 8。az login 済み。
# 値は scripts/deploy.env(scripts/deploy.env.example を参照)から読む。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
CURRENT_STEP="init"
trap 'echo ">>> FAILED at step: $CURRENT_STEP" >&2' ERR

log() { echo "=== $* ==="; }

# --- 0. preflight ---
preflight() {
  CURRENT_STEP="preflight"
  for bin in az func dotnet curl; do
    command -v "$bin" >/dev/null 2>&1 || { echo "ERROR: '$bin' が見つかりません。" >&2; exit 1; }
  done
  if [ ! -f "$SCRIPT_DIR/deploy.env" ]; then
    echo "ERROR: $SCRIPT_DIR/deploy.env がありません。deploy.env.example をコピーして値を埋めてください。" >&2
    exit 1
  fi
  # shellcheck disable=SC1091
  set -a; . "$SCRIPT_DIR/deploy.env"; set +a
  : "${SUBSCRIPTION_ID:?deploy.env に SUBSCRIPTION_ID が必要}"
  : "${RG:?}" "${LOCATION:?}" "${PREFIX:?}" "${SQL_ADMIN:?}" "${SQL_PASSWORD:?}"
  : "${SEED_WEBHOOK_URL:?}" "${DEVICE_ID:?}"
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
deploy_bicep() {
  CURRENT_STEP="bicep"
  set +x
  az deployment group create -g "$RG" -n main \
    -f "$REPO_ROOT/infra/main.bicep" \
    -p namePrefix="$PREFIX" sqlAdminLogin="$SQL_ADMIN" sqlAdminPassword="$SQL_PASSWORD" \
    -o none
  FUNC=$(az deployment group show -g "$RG" -n main --query properties.outputs.functionAppName.value -o tsv | tr -d '\r\n')
  SQLFQDN=$(az deployment group show -g "$RG" -n main --query properties.outputs.sqlServerFqdn.value -o tsv | tr -d '\r\n')
  SQLDB=$(az deployment group show -g "$RG" -n main --query properties.outputs.sqlDatabaseName.value -o tsv | tr -d '\r\n')
  IOT=$(az deployment group show -g "$RG" -n main --query properties.outputs.iotHubName.value -o tsv | tr -d '\r\n')
  log "bicep deployed: FUNC=$FUNC IOT=$IOT SQL=$SQLFQDN/$SQLDB"
}

# --- 3. IoT 接続文字列を app 設定へ ---
set_iot_connection() {
  CURRENT_STEP="iot-connection"
  local conn
  conn=$(az iot hub connection-string show -g "$RG" --hub-name "$IOT" --default-eventhub -o tsv | tr -d '\r\n')
  az functionapp config appsettings set -g "$RG" -n "$FUNC" \
    --settings "IoTHubEventHubConnection=$conn" -o none
  az functionapp restart -g "$RG" -n "$FUNC" -o none
  log "IoT connection set on $FUNC"
}

# --- 4. SQL ファイアウォール(作業者の現IP) ---
open_sql_firewall() {
  CURRENT_STEP="sql-firewall"
  local myip server
  myip=$(curl -s https://ifconfig.me | tr -d '\r\n')
  server="${SQLFQDN%%.*}"
  # create-or-update: 既存でも同名なら更新で冪等
  az sql server firewall-rule create -g "$RG" -s "$server" -n deploy-operator-ip \
    --start-ip-address "$myip" --end-ip-address "$myip" -o none
  log "SQL firewall allows $myip"
}

# --- 5/6. migrate + seed(dotnet ツール) ---
run_migrations_and_seed() {
  CURRENT_STEP="migrate-seed"
  set +x
  export EPCF_SQL_CONNECTION="Server=tcp:${SQLFQDN},1433;Database=${SQLDB};User ID=${SQL_ADMIN};Password=${SQL_PASSWORD};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
  dotnet run --project "$REPO_ROOT/tools/EpcForwarder.Migrate" -c Release -- migrate
  SEED_WEBHOOK_URL="$SEED_WEBHOOK_URL" \
    dotnet run --project "$REPO_ROOT/tools/EpcForwarder.Migrate" -c Release -- seed
  unset EPCF_SQL_CONNECTION
  log "migrations + seed applied"
}

# --- 7. Functions 発行 ---
publish_functions() {
  CURRENT_STEP="publish"
  ( cd "$REPO_ROOT/src/EpcForwarder.Functions" && func azure functionapp publish "$FUNC" --dotnet-isolated )
  log "functions published"
}

# --- 8. デバイス登録(冪等) ---
ensure_device() {
  CURRENT_STEP="device"
  if az iot hub device-identity show --hub-name "$IOT" --device-id "$DEVICE_ID" -o none 2>/dev/null; then
    log "device $DEVICE_ID exists"
  else
    az iot hub device-identity create --hub-name "$IOT" --device-id "$DEVICE_ID" -o none
    log "device $DEVICE_ID created"
  fi
}

main() {
  preflight
  ensure_rg
  deploy_bicep
  set_iot_connection
  open_sql_firewall
  run_migrations_and_seed
  publish_functions
  ensure_device
  log "Provisioning complete. FUNC=$FUNC IOT=$IOT"
}

main "$@"
