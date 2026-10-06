#!/usr/bin/env bash
# File: scripts/create-databases.sh
# Creates CatalogDb, InventoryDb, PaymentDb and OrderDb by running sql/01..04 with sqlcmd
# (the same scripts you can open and execute in SSMS).
#
# Usage:
#   SQL_SERVER=localhost SQL_USER=sa SQL_PASSWORD='Your_strong_pwd1' ./scripts/create-databases.sh
#   SQL_SERVER='(localdb)\mssqllocaldb' ./scripts/create-databases.sh        # Windows auth (Git Bash / WSL interop)
#
# Requires sqlcmd (mssql-tools18 or the Go-based "sqlcmd"). -C trusts the server certificate (dev only).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SQL_SERVER="${SQL_SERVER:-localhost}"

if ! command -v sqlcmd >/dev/null 2>&1; then
  echo "sqlcmd not found. Install mssql-tools18 (https://learn.microsoft.com/sql/tools/sqlcmd/sqlcmd-utility) or run the scripts in SSMS." >&2
  exit 1
fi

AUTH=(-E)
if [[ -n "${SQL_USER:-}" ]]; then
  AUTH=(-U "$SQL_USER" -P "${SQL_PASSWORD:?SQL_PASSWORD is required when SQL_USER is set}")
fi

for script in 01_CatalogDb.sql 02_InventoryDb.sql 03_PaymentDb.sql 04_OrderDb.sql; do
  echo "==> $script on $SQL_SERVER"
  sqlcmd -S "$SQL_SERVER" "${AUTH[@]}" -C -b -i "$ROOT/sql/$script"
done

echo "All four databases created and seeded."
