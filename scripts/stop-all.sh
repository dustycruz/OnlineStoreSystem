#!/usr/bin/env bash
# File: scripts/stop-all.sh
# Stops applications started by scripts/run-all.sh. Pass names to stop only some of them:
#   ./scripts/stop-all.sh catalog        # e.g. to demonstrate the retry / 503 behaviour of OrderService
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_DIR="$ROOT/.run"
names=("$@")
[[ ${#names[@]} -eq 0 ]] && names=(storefront order payment inventory catalog)

for name in "${names[@]}"; do
  pid_file="$RUN_DIR/$name.pid"
  if [[ -f "$pid_file" ]]; then
    pid="$(cat "$pid_file")"
    if kill "$pid" 2>/dev/null; then
      echo "stopped $name (pid $pid)"
    else
      echo "$name was not running"
    fi
    rm -f "$pid_file"
  else
    echo "$name: no pid file"
  fi
done
