# File: scripts/stop-all.ps1
# Stops applications started by scripts\run-all.ps1. Pass names to stop only some of them:
#   .\scripts\stop-all.ps1 catalog        # e.g. to demonstrate the retry / 503 behaviour of OrderService
param([string[]] $Names = @('storefront', 'order', 'payment', 'inventory', 'catalog'))

$runDir = Join-Path (Split-Path -Parent $PSScriptRoot) '.run'
foreach ($name in $Names) {
    $pidFile = Join-Path $runDir "$name.pid"
    if (Test-Path $pidFile) {
        $processId = [int](Get-Content $pidFile)
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($process) {
            Stop-Process -Id $processId -Force
            Write-Host "stopped $name (pid $processId)"
        }
        else {
            Write-Host "$name was not running"
        }
        Remove-Item $pidFile
    }
    else {
        Write-Host "${name}: no pid file"
    }
}
