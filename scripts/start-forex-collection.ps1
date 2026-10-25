param(
    [ValidateSet(7497, 4002)][int] $PaperPort = 7497,
    [ValidateRange(1, 2147483647)][int] $ClientId = 96,
    [ValidateRange(1024, 65535)][int] $WebPort = 5098,
    [string] $RuntimeDirectory,
    [switch] $Background
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = if ($RuntimeDirectory) {
    if (-not [IO.Path]::IsPathFullyQualified($RuntimeDirectory)) { throw 'RuntimeDirectory must be absolute.' }
    [IO.Path]::GetFullPath($RuntimeDirectory)
} else { Join-Path $repoRoot 'data/eurusd-collection' }
$appRoot = Join-Path $runtimeRoot 'app'
$appDll = Join-Path $appRoot 'TradingBot.Web.dll'
$statePath = Join-Path $runtimeRoot 'collector.json'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

# Do not republish over the running dedicated host, including one started in foreground.
$running = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($appDll, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'The EUR/USD collector is already running. Keep that instance; do not start a duplicate.' }
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $WebPort)
$listener.ExclusiveAddressUse = $true
try { $listener.Start() } finally { $listener.Stop() }

New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
dotnet publish (Join-Path $repoRoot 'TradingBot.Web/TradingBot.Web.csproj') -c Release -o $appRoot
if ($LASTEXITCODE -ne 0) { throw 'Collection host publish failed; nothing was started.' }

# Command-line guards override inherited environment settings. All broker mutations are blocked.
# The dedicated working directory keeps the SQLite/Parquet paths stable across restarts.
$collectorArgs = @(
    $appDll, '--environment=ForexCollection', "--contentRoot=$appRoot",
    "--urls=http://127.0.0.1:$WebPort", '--IbkrSettings:Host=127.0.0.1',
    "--IbkrSettings:Paper:Port=$PaperPort", "--IbkrSettings:ClientId=$ClientId",
    '--IbkrSettings:ReadOnlyApi=true', '--TradingSettings:Enabled=false',
    '--TradingSettings:OperatingMode=AnalysisOnly', '--TradingSettings:LiveTradingExplicitlyEnabled=false',
    '--TradingSettings:ForexPaperExecutionEnabled=false', '--ForexResearch:AutomaticPaperSignalsEnabled=false',
    '--ConnectionStrings:TradingBot=Data Source=trading.db', '--DatabaseSettings:ConnectionString=Data Source=trading.db',
    '--DatasetStorage:RootPath=datasets', '--DatasetStorage:Enabled=true',
    '--HistoricalBackfill:Enabled=true', '--MarketDataCollection:Enabled=true'
)
if ($Background) {
    # Start-Process joins arguments; quote each path/value without passing through another shell.
    $quotedArgs = $collectorArgs | ForEach-Object {
        if ($_.Contains('"')) { throw 'Unsupported quote in collector argument.' }
        '"' + $_ + '"'
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $process = Start-Process -FilePath $dotnet -ArgumentList $quotedArgs -WorkingDirectory $runtimeRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runtimeRoot "stdout-$stamp.log") -RedirectStandardError (Join-Path $runtimeRoot "stderr-$stamp.log")
    @{ ProcessId = $process.Id; StartedAtUtc = [DateTime]::UtcNow.ToString('O'); Url = "http://127.0.0.1:$WebPort";
        Database = (Join-Path $runtimeRoot 'trading.db'); Dataset = (Join-Path $runtimeRoot 'datasets'); ReadOnlyApi = $true } |
        ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
    Write-Output "Collector process $($process.Id) started; verify health at http://127.0.0.1:$WebPort/api/health/ready. Runtime: $runtimeRoot"
} else {
    Write-Output "Collecting with order mutations blocked. Ctrl+C stops gracefully. Runtime: $runtimeRoot"
    Push-Location $runtimeRoot
    try { & $dotnet @collectorArgs } finally { Pop-Location }
}
