# File: scripts/demo.ps1
# Scripted verification of the four required scenarios (PowerShell 7+). Start the system first (scripts\run-all.ps1).
#
#   .\scripts\demo.ps1 happy           # 1. order Completed, stock decremented, payment recorded
#   .\scripts\demo.ps1 compensation    # 2. payment declined -> ReleaseStock compensation -> order Cancelled
#   .\scripts\demo.ps1 retry           # 3a. CatalogService transient 503s -> retries with backoff -> success
#   .\scripts\demo.ps1 outage          # 3b. CatalogService down -> retries with backoff -> 503 problem details
#   .\scripts\demo.ps1 trace <id>      # 4. every log line of every service for one X-Correlation-Id
#   .\scripts\demo.ps1 all             # 1 + 2 + 3b + 4
param(
    [ValidateSet('happy', 'compensation', 'retry', 'outage', 'trace', 'all')]
    [string] $Scenario = 'all',
    [string] $CorrelationId,
    [string] $CatalogApi = 'http://localhost:5001',
    [string] $OrderApi = 'http://localhost:5002',
    [string] $InventoryApi = 'http://localhost:5003',
    [string] $PaymentApi = 'http://localhost:5004',
    [string] $LogDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logDir = if ($LogDir) { $LogDir } else { Join-Path $root 'logs' }
$script:LastCorrelationId = $null

function Write-Title([string] $text) { Write-Host "`n$text" -ForegroundColor Cyan }

function New-CorrelationId([string] $name) { "demo-$name-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" }

function Get-Stock([int] $productId) {
    $item = Invoke-RestMethod "$InventoryApi/api/v1/inventory/$productId"
    "onHand=$($item.quantityOnHand) reserved=$($item.quantityReserved) available=$($item.quantityAvailable)"
}

function Invoke-Checkout([string] $correlationId, [hashtable] $body) {
    Invoke-RestMethod -Method Post -Uri "$OrderApi/api/v1/orders/checkout" -ContentType 'application/json' `
        -Headers @{ 'X-Correlation-Id' = $correlationId } -Body ($body | ConvertTo-Json -Depth 5)
}

function Wait-OrderFinal([string] $orderId) {
    for ($i = 0; $i -lt 40; $i++) {
        $order = Invoke-RestMethod "$OrderApi/api/v1/orders/$orderId"
        $final = $order.status -in 'Completed', 'Cancelled' -and $order.saga.status -in 'Completed', 'Compensated', 'Failed'
        if ($final) { return $order }
        Start-Sleep -Milliseconds 500
    }
    return $order
}

function Show-Order($order) {
    Write-Host ("order {0}: status={1} total={2} {3} failureReason={4}" -f $order.orderNumber, $order.status, $order.totalAmount, $order.currency, $order.failureReason)
    Write-Host ("saga  status={0} currentStep={1}" -f $order.saga.status, $order.saga.currentStep)
    foreach ($step in $order.saga.steps) {
        Write-Host ("   {0,-17} {1,-12} {2}" -f $step.stepName, $step.outcome, $step.detail)
    }
}

function Invoke-Scenario-Happy {
    Write-Title 'SCENARIO 1 - HAPPY PATH (2 x product 1 + 1 x product 2, card 4242...)'
    Write-Host "Stock BEFORE product 1: $(Get-Stock 1)"
    Write-Host "Stock BEFORE product 2: $(Get-Stock 2)"
    $cid = New-CorrelationId 'happy'
    Write-Host "X-Correlation-Id: $cid"
    $accepted = Invoke-Checkout $cid @{
        customerId = 'cust-demo-1'; customerEmail = 'alice@example.com'
        items = @(@{ productId = 1; quantity = 2 }, @{ productId = 2; quantity = 1 })
        payment = @{ cardNumber = '4242424242424242'; cardHolderName = 'Alice Doe'; expiryMonth = 12; expiryYear = 2030 }
    }
    Write-Host "202 Accepted -> order $($accepted.orderId) is $($accepted.status); polling..."
    Show-Order (Wait-OrderFinal $accepted.orderId)
    Write-Host "Stock AFTER  product 1: $(Get-Stock 1)"
    Write-Host "Stock AFTER  product 2: $(Get-Stock 2)"
    Write-Host 'Payment recorded:'
    Invoke-RestMethod "$PaymentApi/api/v1/payments?orderId=$($accepted.orderId)" | ConvertTo-Json -Depth 5
    $script:LastCorrelationId = $cid
}

function Invoke-Scenario-Compensation {
    Write-Title 'SCENARIO 2 - COMPENSATION (1 x product 3, declined card 4000 0000 0000 0002)'
    Write-Host "Stock BEFORE product 3: $(Get-Stock 3)"
    $cid = New-CorrelationId 'compensation'
    Write-Host "X-Correlation-Id: $cid"
    $accepted = Invoke-Checkout $cid @{
        customerId = 'cust-demo-2'; customerEmail = 'bob@example.com'
        items = @(@{ productId = 3; quantity = 1 })
        payment = @{ cardNumber = '4000000000000002'; cardHolderName = 'Bob Smith'; expiryMonth = 11; expiryYear = 2029 }
    }
    Show-Order (Wait-OrderFinal $accepted.orderId)
    Write-Host "Stock AFTER  product 3: $(Get-Stock 3)  (reserved, then released by the compensation)"
    Write-Host 'Reservations (status must be Released):'
    Invoke-RestMethod "$InventoryApi/api/v1/inventory/reservations/$($accepted.orderId)" | ConvertTo-Json -Depth 5

    Write-Title 'SCENARIO 2b - COMPENSATION via amount 9999.00 (1 x Cinema Camera Pro 8K)'
    $cid = New-CorrelationId 'compensation9999'
    $accepted = Invoke-Checkout $cid @{
        customerId = 'cust-demo-3'; customerEmail = 'carol@example.com'
        items = @(@{ productId = 5; quantity = 1 })
        payment = @{ cardNumber = '4242424242424242'; cardHolderName = 'Carol King'; expiryMonth = 1; expiryYear = 2031 }
    }
    Show-Order (Wait-OrderFinal $accepted.orderId)
    $script:LastCorrelationId = $cid
}

function Invoke-CheckoutRaw([string] $cid, [int] $productId) {
    $body = @{
        customerId = 'cust-demo-4'; customerEmail = 'erin@example.com'
        items = @(@{ productId = $productId; quantity = 1 })
        payment = @{ cardNumber = '4242424242424242'; cardHolderName = 'Erin Lee'; expiryMonth = 1; expiryYear = 2031 }
    } | ConvertTo-Json -Depth 5
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Method Post -Uri "$OrderApi/api/v1/orders/checkout" -ContentType 'application/json' `
            -Headers @{ 'X-Correlation-Id' = $cid } -Body $body -UseBasicParsing
        Write-Host "HTTP $($response.StatusCode) after $([int]$watch.Elapsed.TotalMilliseconds) ms"
    }
    catch {
        Write-Host "HTTP $([int]$_.Exception.Response.StatusCode) after $([int]$watch.Elapsed.TotalMilliseconds) ms"
        Write-Host $_.ErrorDetails.Message
    }
}

function Show-LogLines([string] $cid, [string] $pattern, [string[]] $files) {
    Get-ChildItem $files -ErrorAction SilentlyContinue |
        Select-String -Pattern "cid:$cid" -SimpleMatch |
        Where-Object { $_.Line -match $pattern } |
        ForEach-Object { $_.Line } | Sort-Object
}

function Invoke-Scenario-Retry {
    Write-Title 'SCENARIO 3a - RESILIENT RETRY (CatalogService fails the first 2 attempts with 503)'
    & (Join-Path $PSScriptRoot 'stop-all.ps1') catalog | Out-Null
    & (Join-Path $PSScriptRoot 'run-all.ps1') -CatalogFaults | Out-Null
    $cid = New-CorrelationId 'retry'
    Write-Host "X-Correlation-Id: $cid"
    Invoke-CheckoutRaw $cid 4
    Start-Sleep -Seconds 1
    Show-LogLines $cid 'RESILIENCE|FAULT INJECTION|responded' @("$logDir/order-service-*.log", "$logDir/catalog-service-*.log")
    & (Join-Path $PSScriptRoot 'stop-all.ps1') catalog | Out-Null
    & (Join-Path $PSScriptRoot 'run-all.ps1') | Out-Null
    $script:LastCorrelationId = $cid
}

function Invoke-Scenario-Outage {
    Write-Title 'SCENARIO 3b - CATALOG DOWN (retries with exponential backoff, then RFC 7807 503)'
    & (Join-Path $PSScriptRoot 'stop-all.ps1') catalog | Out-Null
    Start-Sleep -Seconds 1
    $cid = New-CorrelationId 'outage'
    Write-Host "X-Correlation-Id: $cid"
    Invoke-CheckoutRaw $cid 4
    Show-LogLines $cid 'RESILIENCE|unreachable|responded' @("$logDir/order-service-*.log")
    & (Join-Path $PSScriptRoot 'run-all.ps1') | Out-Null
    $script:LastCorrelationId = $cid
}

function Invoke-Scenario-Trace([string] $cid) {
    if (-not $cid) { throw 'Pass -CorrelationId <id> (or run another scenario first).' }
    Write-Title "SCENARIO 4 - TRACING: every log line with X-Correlation-Id = $cid"
    foreach ($file in Get-ChildItem "$logDir/*-????????.log") {
        $count = (Select-String -Path $file -Pattern "cid:$cid" -SimpleMatch).Count
        Write-Host ("  {0,-40} {1,4} lines" -f $file.Name, $count)
    }
    Show-LogLines $cid '.' @("$logDir/*-????????.log")
}

switch ($Scenario) {
    'happy'        { Invoke-Scenario-Happy }
    'compensation' { Invoke-Scenario-Compensation }
    'retry'        { Invoke-Scenario-Retry }
    'outage'       { Invoke-Scenario-Outage }
    'trace'        { Invoke-Scenario-Trace $CorrelationId }
    'all' {
        Invoke-Scenario-Happy
        $happy = $script:LastCorrelationId
        Invoke-Scenario-Compensation
        Invoke-Scenario-Outage
        Invoke-Scenario-Trace $happy
    }
}
