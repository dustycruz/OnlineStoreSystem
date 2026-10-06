# File: scripts/create-databases.ps1
# Creates CatalogDb, InventoryDb, PaymentDb and OrderDb by running sql/01..04 with sqlcmd
# (the same scripts you can open and execute in SSMS).
#
# Usage (PowerShell 7+ or Windows PowerShell 5.1):
#   .\scripts\create-databases.ps1                                   # (localdb)\mssqllocaldb, Windows auth
#   .\scripts\create-databases.ps1 -Server '.\SQLEXPRESS'
#   .\scripts\create-databases.ps1 -Server 'localhost,1433' -User sa -Password 'Your_strong_pwd1'
param(
    [string] $Server = '(localdb)\mssqllocaldb',
    [string] $User,
    [string] $Password
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'sqlcmd not found. Install the SQL Server command-line tools or run the scripts in SSMS (File > Open > F5).'
}

if ($Server -like '(localdb)*') {
    # Make sure the default LocalDB instance exists and is running.
    sqllocaldb create MSSQLLocalDB 2>$null | Out-Null
    sqllocaldb start MSSQLLocalDB | Out-Null
}

$auth = if ($User) { @('-U', $User, '-P', $Password) } else { @('-E') }

foreach ($script in '01_CatalogDb.sql', '02_InventoryDb.sql', '03_PaymentDb.sql', '04_OrderDb.sql') {
    Write-Host "==> $script on $Server" -ForegroundColor Cyan
    & sqlcmd -S $Server @auth -C -b -i (Join-Path $root "sql\$script")
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed for $script" }
}

Write-Host 'All four databases created and seeded.' -ForegroundColor Green
