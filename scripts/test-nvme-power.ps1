param(
    [switch] $Run,
    [ValidateRange(1, 30)][int] $Minutes = 10
)

# Opt-in machine diagnostic; never starts the bot or changes SQLite settings.
$ErrorActionPreference = 'Stop'
function Invoke-PowerConfig([string[]] $Arguments) {
    $result = & powercfg.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "powercfg failed ($LASTEXITCODE): $result" }
    return ($result -join "`n")
}
function Get-ActivePlan {
    $result = Invoke-PowerConfig @('/getactivescheme')
    $match = [regex]::Match($result, '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}')
    if (-not $match.Success) { throw 'Cannot identify the active power plan.' }
    return $match.Value.ToLowerInvariant()
}

$originalPlan = Get-ActivePlan
if (-not $Run) {
    Write-Output "Preview only. With -Run, clone plan $originalPlan, set ONLY AC primary/secondary NVMe transition latency tolerance to 0 ms for $Minutes minutes, then restore the original plan. Battery/ASPM/SQLite settings are unchanged. Keep the laptop on AC and this window open."
    return
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this diagnostic from an administrator PowerShell. No settings changed.'
}

$testPlan = [guid]::NewGuid().ToString()
$resultRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "TestResults/nvme-power-$testPlan"
New-Item -ItemType Directory -Path $resultRoot -ErrorAction Stop | Out-Null
$resultPath = Join-Path $resultRoot 'session.json'
$state = [ordered]@{
    OriginalPlan = $originalPlan; TestPlan = $testPlan; Minutes = $Minutes
    StartedUtc = $null; EndedUtc = $null; OriginalRestored = $false
    TestPlanDeleted = $false; Error = $null; CleanupError = $null
}
$created = $false
try {
    Invoke-PowerConfig @('/duplicatescheme', $originalPlan, $testPlan) | Out-Null
    $created = $true
    Invoke-PowerConfig @('/changename', $testPlan, 'aiTradingBot temporary NVMe diagnostic') | Out-Null
    foreach ($setting in @('fc95af4d-40e7-4b6d-835a-56d131dbc80e', 'dbc9e238-6de9-49e3-92cd-8c2b4946b472')) {
        Invoke-PowerConfig @('/setacvalueindex', $testPlan, 'SUB_DISK', $setting, '0') | Out-Null
    }
    Invoke-PowerConfig @('/setactive', $testPlan) | Out-Null
    if ((Get-ActivePlan) -ne $testPlan) { throw 'Test power plan did not become active.' }
    $state.StartedUtc = [DateTime]::UtcNow.ToString('O')
    $state | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output "Test started: $($state.StartedUtc). Keep this window open for $Minutes minutes. No WPR recording or bot restart is needed."
    Write-Output "Emergency restoration if this window is forcibly closed: powercfg /setactive $originalPlan"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalMinutes -lt $Minutes) { Start-Sleep -Seconds 1 }
} catch {
    $state.Error = $_.Exception.Message
    throw
} finally {
    $state.EndedUtc = [DateTime]::UtcNow.ToString('O')
    if ($created) {
        try {
            $active = Get-ActivePlan
            if ($active -eq $testPlan) {
                Invoke-PowerConfig @('/setactive', $originalPlan) | Out-Null
            }
            $active = Get-ActivePlan
            $state.OriginalRestored = $active -eq $originalPlan
            # Preserve a plan explicitly selected by the operator during the test.
            if ($active -eq $testPlan) { throw 'Temporary plan remains active; do not delete it.' }
            Invoke-PowerConfig @('/delete', $testPlan) | Out-Null
            $state.TestPlanDeleted = $true
        } catch {
            $state.CleanupError = $_.Exception.Message
            Write-Warning "Cleanup failed. Restore manually: powercfg /setactive $originalPlan"
        }
    }
    $state | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output "Diagnostic result: $resultPath"
    Write-Output "Original restored: $($state.OriginalRestored); temporary plan removed: $($state.TestPlanDeleted)"
}
