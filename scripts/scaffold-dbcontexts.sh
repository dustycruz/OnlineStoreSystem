#!/usr/bin/env bash
# File: scripts/scaffold-dbcontexts.sh
# DATABASE-FIRST: reverse-engineers the four SQL Server databases into their services with EF Core.
# Re-run after changing any sql/0x_*.sql script. Hand-written customisations live in separate
# partial classes (e.g. InventoryDbContext.Customizations.cs) and survive --force.
#
# Usage:
#   ./scripts/scaffold-dbcontexts.sh                                        # (localdb)\mssqllocaldb, Windows auth
#   SQL_CONNECTION_TEMPLATE='Server=localhost,1433;Database={db};User Id=sa;Password=Your_strong_pwd1;TrustServerCertificate=True' \
#     ./scripts/scaffold-dbcontexts.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

TEMPLATE="${SQL_CONNECTION_TEMPLATE:-Server=(localdb)\\mssqllocaldb;Database={db};Trusted_Connection=True;TrustServerCertificate=True}"

dotnet tool restore

scaffold() {
  local service="$1" database="$2" context="$3"
  local project="src/OnlineStoreSystem.${service}"
  local connection="${TEMPLATE//\{db\}/$database}"
  echo "==> Scaffolding $database into $project ($context)"
  dotnet ef dbcontext scaffold "$connection" Microsoft.EntityFrameworkCore.SqlServer \
    --project "$project" \
    --startup-project "$project" \
    --context "$context" \
    --context-dir Data \
    --output-dir Models \
    --namespace "OnlineStoreSystem.${service}.Models" \
    --context-namespace "OnlineStoreSystem.${service}.Data" \
    --no-onconfiguring \
    --force
}

scaffold CatalogService   CatalogDb   CatalogDbContext
scaffold InventoryService InventoryDb InventoryDbContext
scaffold PaymentService   PaymentDb   PaymentDbContext
scaffold OrderService     OrderDb     OrderDbContext

echo "Scaffolding complete. Run 'dotnet build OnlineStoreSystem.sln' to verify."
