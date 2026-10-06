#!/usr/bin/env bash
# File: scripts/demo.sh
# Scripted verification of the four required scenarios with curl. Start the system first (scripts/run-all.sh).
#
#   ./scripts/demo.sh happy          # 1. order Completed, stock decremented, payment recorded
#   ./scripts/demo.sh compensation   # 2. payment declined -> ReleaseStock compensation -> order Cancelled
#   ./scripts/demo.sh retry          # 3a. CatalogService transient 503s -> retries with backoff -> success
#   ./scripts/demo.sh outage         # 3b. CatalogService down -> retries with backoff -> 503 problem details
#   ./scripts/demo.sh trace <id>     # 4. every log line of every service for one X-Correlation-Id
#   ./scripts/demo.sh all            # 1 + 2 + 3b + 4
#
# Optional: CATALOG_STOP_CMD / CATALOG_START_CMD / CATALOG_FAULTS_START_CMD override how CatalogService is
# stopped / restarted (defaults use scripts/stop-all.sh and scripts/run-all.sh).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CATALOG_API="${CATALOG_API:-http://localhost:5001}"
ORDER_API="${ORDER_API:-http://localhost:5002}"
INVENTORY_API="${INVENTORY_API:-http://localhost:5003}"
PAYMENT_API="${PAYMENT_API:-http://localhost:5004}"
CATALOG_STOP_CMD="${CATALOG_STOP_CMD:-$ROOT/scripts/stop-all.sh catalog}"
CATALOG_START_CMD="${CATALOG_START_CMD:-$ROOT/scripts/run-all.sh}"
CATALOG_FAULTS_START_CMD="${CATALOG_FAULTS_START_CMD:-env CATALOG_FAULTS=1 $ROOT/scripts/run-all.sh}"
LOG_DIR="${LOG_DIR:-$ROOT/logs}"

bold() { printf '\n\033[1m%s\033[0m\n' "$*"; }
pretty() {
  if command -v jq >/dev/null 2>&1; then jq .
  elif command -v python3 >/dev/null 2>&1; then python3 -m json.tool
  else cat; echo
  fi
}
# First occurrence of a string field in a JSON document (good enough for orderId / status).
field() { grep -o "\"$1\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }
nth_field() { grep -o "\"$1\":\"[^\"]*\"" | sed -n "${2}p" | cut -d'"' -f4; }
stock() { curl -s "$INVENTORY_API/api/v1/inventory/$1" | grep -o '"quantity[A-Za-z]*":[0-9]*' | tr '\n' ' '; echo; }
new_cid() { echo "demo-$1-$(date +%s)"; }

checkout() { # $1 correlation id, $2 JSON body
  curl -s -X POST "$ORDER_API/api/v1/orders/checkout" \
    -H 'Content-Type: application/json' -H "X-Correlation-Id: $1" -d "$2"
}

wait_for_final() { # $1 orderId -> prints the final order JSON
  local order_json status saga_status
  for _ in $(seq 1 40); do
    order_json="$(curl -s "$ORDER_API/api/v1/orders/$1")"
    status="$(echo "$order_json" | field status)"
    saga_status="$(echo "$order_json" | nth_field status 2)"
    if [[ "$status" =~ ^(Completed|Cancelled)$ && "$saga_status" =~ ^(Completed|Compensated|Failed)$ ]]; then
      echo "$order_json"
      return
    fi
    sleep 0.5
  done
  echo "$order_json"
}

SUMMARY_PY=$(cat <<'PY'
import json, sys
o = json.load(sys.stdin)
s = o.get('saga') or {}
print('order %s: status=%s total=%s %s failureReason=%s' % (o['orderNumber'], o['status'], o['totalAmount'], o['currency'], o['failureReason']))
print('saga  status=%s currentStep=%s' % (s.get('status'), s.get('currentStep')))
for st in s.get('steps', []):
    print('   %s  %-17s %-12s %s' % (st['occurredAtUtc'][11:23], st['stepName'], st['outcome'], st.get('detail') or ''))
PY
)

summarise() { # order JSON on stdin
  if command -v python3 >/dev/null 2>&1; then
    python3 -c "$SUMMARY_PY"
  else
    pretty
  fi
}

scenario_happy() {
  bold "SCENARIO 1 - HAPPY PATH (2 x product 1 + 1 x product 2, card 4242...)"
  echo "Stock BEFORE  product 1: $(stock 1)"
  echo "Stock BEFORE  product 2: $(stock 2)"
  local cid; cid="$(new_cid happy)"
  echo "X-Correlation-Id: $cid"
  local response order_id
  response="$(checkout "$cid" '{"customerId":"cust-demo-1","customerEmail":"alice@example.com","items":[{"productId":1,"quantity":2},{"productId":2,"quantity":1}],"payment":{"cardNumber":"4242424242424242","cardHolderName":"Alice Doe","expiryMonth":12,"expiryYear":2030}}')"
  order_id="$(echo "$response" | field orderId)"
  echo "202 Accepted -> order $order_id is $(echo "$response" | field status); polling..."
  wait_for_final "$order_id" | summarise
  echo "Stock AFTER   product 1: $(stock 1)"
  echo "Stock AFTER   product 2: $(stock 2)"
  echo "Payment recorded:"
  curl -s "$PAYMENT_API/api/v1/payments?orderId=$order_id" | pretty
  echo "Reservations:"
  curl -s "$INVENTORY_API/api/v1/inventory/reservations/$order_id" | pretty
  LAST_CID="$cid"
}

scenario_compensation() {
  bold "SCENARIO 2 - COMPENSATION (1 x product 3, declined card 4000 0000 0000 0002)"
  echo "Stock BEFORE  product 3: $(stock 3)"
  local cid; cid="$(new_cid compensation)"
  echo "X-Correlation-Id: $cid"
  local response order_id
  response="$(checkout "$cid" '{"customerId":"cust-demo-2","customerEmail":"bob@example.com","items":[{"productId":3,"quantity":1}],"payment":{"cardNumber":"4000000000000002","cardHolderName":"Bob Smith","expiryMonth":11,"expiryYear":2029}}')"
  order_id="$(echo "$response" | field orderId)"
  echo "202 Accepted -> order $order_id; polling..."
  wait_for_final "$order_id" | summarise
  echo "Stock AFTER   product 3: $(stock 3)   (reserved, then released by the compensation)"
  echo "Reservations (status must be Released):"
  curl -s "$INVENTORY_API/api/v1/inventory/reservations/$order_id" | pretty
  echo "Declined payment attempt:"
  curl -s "$PAYMENT_API/api/v1/payments?orderId=$order_id" | pretty

  bold "SCENARIO 2b - COMPENSATION via amount 9999.00 (1 x Cinema Camera Pro 8K, approved card)"
  cid="$(new_cid compensation9999)"
  response="$(checkout "$cid" '{"customerId":"cust-demo-3","customerEmail":"carol@example.com","items":[{"productId":5,"quantity":1}],"payment":{"cardNumber":"4242424242424242","cardHolderName":"Carol King","expiryMonth":1,"expiryYear":2031}}')"
  order_id="$(echo "$response" | field orderId)"
  wait_for_final "$order_id" | summarise
  LAST_CID="$cid"
}

scenario_retry() {
  bold "SCENARIO 3a - RESILIENT RETRY (CatalogService fails the first 2 attempts with 503)"
  echo "Restarting CatalogService with --FaultInjection:Enabled=true ..."
  eval "$CATALOG_STOP_CMD" >/dev/null || true
  eval "$CATALOG_FAULTS_START_CMD" >/dev/null
  local cid; cid="$(new_cid retry)"
  echo "X-Correlation-Id: $cid"
  local start end
  start=$(date +%s)
  curl -s -o /dev/null -w "HTTP %{http_code} after %{time_total}s\n" -X POST "$ORDER_API/api/v1/orders/checkout" \
    -H 'Content-Type: application/json' -H "X-Correlation-Id: $cid" \
    -d '{"customerId":"cust-demo-4","customerEmail":"erin@example.com","items":[{"productId":4,"quantity":1}],"payment":{"cardNumber":"4242424242424242","cardHolderName":"Erin Lee","expiryMonth":1,"expiryYear":2031}}'
  sleep 1
  echo "Log evidence (OrderService retries + CatalogService injected faults):"
  grep -h "cid:$cid" "$LOG_DIR"/order-service-*.log "$LOG_DIR"/catalog-service-*.log 2>/dev/null \
    | grep -E "RESILIENCE|FAULT INJECTION|responded" | sort | cut -c1-230
  echo "Restarting CatalogService without fault injection ..."
  eval "$CATALOG_STOP_CMD" >/dev/null || true
  eval "$CATALOG_START_CMD" >/dev/null
  LAST_CID="$cid"
}

scenario_outage() {
  bold "SCENARIO 3b - CATALOG DOWN (retries with exponential backoff, then RFC 7807 503)"
  echo "Stopping CatalogService ..."
  eval "$CATALOG_STOP_CMD" >/dev/null || true
  sleep 1
  local cid; cid="$(new_cid outage)"
  echo "X-Correlation-Id: $cid"
  curl -s -i -X POST "$ORDER_API/api/v1/orders/checkout" \
    -H 'Content-Type: application/json' -H "X-Correlation-Id: $cid" \
    -d '{"customerId":"cust-demo-5","customerEmail":"frank@example.com","items":[{"productId":4,"quantity":1}],"payment":{"cardNumber":"4242424242424242","cardHolderName":"Frank Moe","expiryMonth":1,"expiryYear":2031}}' \
    | grep -v -E '^(Date|Server|Cache-Control|Expires|Pragma|Transfer-Encoding):'
  echo
  echo "Retry log of OrderService:"
  grep -h "cid:$cid" "$LOG_DIR"/order-service-*.log 2>/dev/null | grep -E "RESILIENCE|unreachable|responded" | cut -c1-230
  echo "Restarting CatalogService ..."
  eval "$CATALOG_START_CMD" >/dev/null
  LAST_CID="$cid"
}

scenario_trace() { # $1 correlation id
  local cid="${1:-${LAST_CID:-}}"
  if [[ -z "$cid" ]]; then
    echo "usage: $0 trace <correlation-id>" >&2
    exit 1
  fi
  bold "SCENARIO 4 - TRACING: every log line with X-Correlation-Id = $cid"
  for file in "$LOG_DIR"/*-????????.log; do
    printf "  %-40s %4s lines\n" "$(basename "$file")" "$(grep -c "cid:$cid" "$file" || true)"
  done
  echo
  grep -h "cid:$cid" "$LOG_DIR"/*-????????.log | sort | cut -c1-220
}

case "${1:-all}" in
  happy)        scenario_happy ;;
  compensation) scenario_compensation ;;
  retry)        scenario_retry ;;
  outage)       scenario_outage ;;
  trace)        scenario_trace "${2:-}" ;;
  all)
    scenario_happy;        happy_cid="$LAST_CID"
    scenario_compensation
    scenario_outage
    scenario_trace "$happy_cid"
    ;;
  *) echo "usage: $0 [happy|compensation|retry|outage|trace <id>|all]" >&2; exit 1 ;;
esac
