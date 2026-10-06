#!/usr/bin/env bash
# File: scripts/run-all.sh
# Builds the solution once and starts all five applications in the background (no Docker):
#   Storefront :5000  Catalog :5001  Order :5002  Inventory :5003  Payment :5004
# Console output -> logs/<app>.console.log, structured Serilog files -> logs/<app>-<date>.log
# Stop everything with ./scripts/stop-all.sh
#
# Optional environment variables:
#   SQL_CONNECTION_TEMPLATE  e.g. 'Server=localhost,1433;Database={db};User Id=sa;Password=Your_strong_pwd1;TrustServerCertificate=True'
#                            ({db} is replaced by CatalogDb / OrderDb / InventoryDb / PaymentDb). Default: appsettings.json (LocalDB).
#   RABBITMQ_CONNECTION      e.g. 'amqps://user:pass@host.rmq.cloudamqp.com/vhost' (default: amqp://guest:guest@localhost:5672/)
#   CATALOG_FAULTS=1         start CatalogService with deterministic transient-fault injection (retry demo)
#   PAYMENT_FAULTS=1         start PaymentService with deterministic transient-fault injection
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_DIR="$ROOT/.run"
mkdir -p "$RUN_DIR" "$ROOT/logs"

echo "==> Building OnlineStoreSystem.sln"
dotnet build "$ROOT/OnlineStoreSystem.sln" -c Debug -v quiet -nologo

start_app() {
  local name="$1" project_dir="$2" assembly="$3" port="$4" database="$5"
  shift 5

  if [[ -f "$RUN_DIR/$name.pid" ]] && kill -0 "$(cat "$RUN_DIR/$name.pid")" 2>/dev/null; then
    echo "    $name already running (pid $(cat "$RUN_DIR/$name.pid"))"
    return
  fi

  local -a env_vars=("ASPNETCORE_ENVIRONMENT=Development")
  if [[ -n "${SQL_CONNECTION_TEMPLATE:-}" && -n "$database" ]]; then
    env_vars+=("ConnectionStrings__${database}=${SQL_CONNECTION_TEMPLATE//\{db\}/$database}")
  fi
  if [[ -n "${RABBITMQ_CONNECTION:-}" ]]; then
    env_vars+=("RabbitMq__ConnectionString=${RABBITMQ_CONNECTION}")
  fi

  # Run the built assembly from the project directory (= content root with appsettings.json / wwwroot).
  (
    cd "$ROOT/$project_dir"
    env "${env_vars[@]}" nohup dotnet "bin/Debug/net10.0/$assembly.dll" "$@" \
      > "$ROOT/logs/$name.console.log" 2>&1 &
    echo $! > "$RUN_DIR/$name.pid"
  )

  printf "    %-10s starting on http://localhost:%s (pid %s) " "$name" "$port" "$(cat "$RUN_DIR/$name.pid")"
  for _ in $(seq 1 60); do
    if curl -fs "http://localhost:$port/health/live" >/dev/null 2>&1; then
      echo "ready"
      return
    fi
    sleep 1
  done
  echo "NOT READY - see logs/$name.console.log"
}

catalog_args=()
[[ "${CATALOG_FAULTS:-0}" == "1" ]] && catalog_args+=("--FaultInjection:Enabled=true")
payment_args=()
[[ "${PAYMENT_FAULTS:-0}" == "1" ]] && payment_args+=("--FaultInjection:Enabled=true")

echo "==> Starting services"
start_app catalog    src/OnlineStoreSystem.CatalogService   OnlineStoreSystem.CatalogService   5001 CatalogDb   ${catalog_args[@]+"${catalog_args[@]}"}
start_app inventory  src/OnlineStoreSystem.InventoryService OnlineStoreSystem.InventoryService 5003 InventoryDb
start_app payment    src/OnlineStoreSystem.PaymentService   OnlineStoreSystem.PaymentService   5004 PaymentDb   ${payment_args[@]+"${payment_args[@]}"}
start_app order      src/OnlineStoreSystem.OrderService     OnlineStoreSystem.OrderService     5002 OrderDb
start_app storefront web/OnlineStoreSystem.Storefront       OnlineStoreSystem.Storefront       5000 ""

echo
echo "==> Readiness (database + RabbitMQ checks):"
for port in 5001 5002 5003 5004 5000; do
  printf "    :%s  %s\n" "$port" "$(curl -s "http://localhost:$port/health" || echo 'unreachable')"
done
echo
echo "Storefront: http://localhost:5000   Swagger: http://localhost:500{1..4}/swagger   RabbitMQ UI: http://localhost:15672"
