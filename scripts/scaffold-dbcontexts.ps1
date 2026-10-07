# File: scripts/scaffold-dbcontexts.ps1
# DATABASE-FIRST: reverse-engineers the four SQL Server databases into their services with EF Core.
# Re-run after changing any sql\0x_*.sql script. Hand-written customisations live in separate
# partial classes (e.g. InventoryDbContext.Customizations.cs) and survive -force.
#
# Usage:
#   .\scripts\scaffold-dbcontexts.ps1
#   .\scripts\scaffold-dbcontexts.ps1 -ConnectionTemplate 'Server=.\SQLEXPRESS;Database={db};Trusted_Connection=True;TrustServerCertificate=True'
param(
    [string] $ConnectionTemplate = 'Server=(localdb)\mssqllocaldb;Database={db};Trusted_Connection=True;TrustServerCertificate=True'
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

$targets = @(
    @{ Service = 'CatalogService';   Database = 'CatalogDb';   Context = 'CatalogDbContext' },
    @{ Service = 'InventoryService'; Database = 'InventoryDb'; Context = 'InventoryDbContext' },
    @{ Service = 'PaymentService';   Database = 'PaymentDb';   Context = 'PaymentDbContext' },
    @{ Service = 'OrderService';     Database = 'OrderDb';     Context = 'OrderDbContext' }
)

foreach ($t in $targets) {
    $project = "src/OnlineStoreSystem.$($t.Service)"
    $connection = $ConnectionTemplate.Replace('{db}', $t.Database)
    Write-Host "==> Scaffolding $($t.Database) into $project ($($t.Context))" -ForegroundColor Cyan

    dotnet ef dbcontext scaffold $connection Microsoft.EntityFrameworkCore.SqlServer `
        --project $project `
        --startup-project $project `
        --context $t.Context `
        --context-dir Data `
        --output-dir Models `
        --namespace "OnlineStoreSystem.$($t.Service).Models" `
        --context-namespace "OnlineStoreSystem.$($t.Service).Data" `
        --no-onconfiguring `
        --force
    if ($LASTEXITCODE -ne 0) { throw "Scaffolding $($t.Database) failed" }
}

Write-Host "Scaffolding complete. Run 'dotnet build OnlineStoreSystem.sln' to verify." -ForegroundColor Green
