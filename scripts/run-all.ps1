# File: scripts/run-all.ps1
# Builds the solution once and starts all five applications (no Docker):
#   Storefront :5000  Catalog :5001  Order :5002  Inventory :5003  Payment :5004
# On Windows every app opens in its own console window (easy to watch the logs); elsewhere they run hidden
# with output in logs\<app>.console.log. Structured Serilog files are written to logs\<app>-<date>.log.
# Stop everything with .\scripts\stop-all.ps1
#
# Examples:
#   .\scripts\run-all.ps1
#   .\scripts\run-all.ps1 -CatalogFaults            # deterministic transient faults in CatalogService (retry demo)
#   .\scripts\run-all.ps1 -SqlConnectionTemplate 'Server=localhost,1433;Database={db};User Id=sa;Password=Your_strong_pwd1;TrustServerCertificate=True'
#   .\scripts\run-all.ps1 -RabbitMqConnection 'amqps://user:pass@host.rmq.cloudamqp.com/vhost'
param(
    [string] $SqlConnectionTemplate,
    [string] $RabbitMqConnection,
    [switch] $CatalogFaults,
    [switch] $PaymentFaults
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $root '.run'
$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Force -Path $runDir, $logDir | Out-Null

Write-Host '==> Building OnlineStoreSystem.sln' -ForegroundColor Cyan
dotnet build (Join-Path $root 'OnlineStoreSystem.sln') -c Debug -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$onWindows = $IsWindows -or ($PSVersionTable.PSEdition -eq 'Desktop')

function Start-App {
    param([string] $Name, [string] $ProjectDir, [string] $Assembly, [int] $Port, [string] $Database, [string[]] $AppArgs = @())

    $pidFile = Join-Path $runDir "$Name.pid"
    if ((Test-Path $pidFile) -and (Get-Process -Id (Get-Content $pidFile) -ErrorAction SilentlyContinue)) {
        Write-Host "    $Name already running"
        return
    }

    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    if ($SqlConnectionTemplate -and $Database) {
        Set-Item -Path "env:ConnectionStrings__$Database" -Value $SqlConnectionTemplate.Replace('{db}', $Database)
    }
    if ($RabbitMqConnection) { $env:RabbitMq__ConnectionString = $RabbitMqConnection }

    $workDir = Join-Path $root $ProjectDir
    $dll = "bin/Debug/net10.0/$Assembly.dll"
    $arguments = @($dll) + $AppArgs

    if ($onWindows) {
        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $workDir -PassThru
    }
    else {
        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $workDir -PassThru `
            -RedirectStandardOutput (Join-Path $logDir "$Name.console.log") `
            -RedirectStandardError (Join-Path $logDir "$Name.console.err.log")
    }
    $process.Id | Set-Content $pidFile

    Write-Host ("    {0,-10} starting on http://localhost:{1} (pid {2}) " -f $Name, $Port, $process.Id) -NoNewline
    for ($i = 0; $i -lt 60; $i++) {
        try {
            Invoke-WebRequest "http://localhost:$Port/health/live" -UseBasicParsing -TimeoutSec 2 | Out-Null
            Write-Host 'ready' -ForegroundColor Green
            return
        }
        catch { Start-Sleep -Seconds 1 }
    }
    Write-Host 'NOT READY - check its console window / logs' -ForegroundColor Red
}

$catalogArgs = if ($CatalogFaults) { @('--FaultInjection:Enabled=true') } else { @() }
$paymentArgs = if ($PaymentFaults) { @('--FaultInjection:Enabled=true') } else { @() }

Write-Host '==> Starting services' -ForegroundColor Cyan
Start-App -Name catalog    -ProjectDir 'src/OnlineStoreSystem.CatalogService'   -Assembly 'OnlineStoreSystem.CatalogService'   -Port 5001 -Database 'CatalogDb'   -AppArgs $catalogArgs
Start-App -Name inventory  -ProjectDir 'src/OnlineStoreSystem.InventoryService' -Assembly 'OnlineStoreSystem.InventoryService' -Port 5003 -Database 'InventoryDb'
Start-App -Name payment    -ProjectDir 'src/OnlineStoreSystem.PaymentService'   -Assembly 'OnlineStoreSystem.PaymentService'   -Port 5004 -Database 'PaymentDb'   -AppArgs $paymentArgs
Start-App -Name order      -ProjectDir 'src/OnlineStoreSystem.OrderService'     -Assembly 'OnlineStoreSystem.OrderService'     -Port 5002 -Database 'OrderDb'
Start-App -Name storefront -ProjectDir 'web/OnlineStoreSystem.Storefront'       -Assembly 'OnlineStoreSystem.Storefront'       -Port 5000 -Database ''

Write-Host "`n==> Readiness (database + RabbitMQ checks):" -ForegroundColor Cyan
foreach ($port in 5001, 5002, 5003, 5004, 5000) {
    try { $health = (Invoke-WebRequest "http://localhost:$port/health" -UseBasicParsing -TimeoutSec 5).Content }
    catch { $health = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { 'unreachable' } }
    Write-Host "    :$port  $health"
}
Write-Host "`nStorefront: http://localhost:5000   Swagger: http://localhost:5001..5004/swagger   RabbitMQ UI: http://localhost:15672"
