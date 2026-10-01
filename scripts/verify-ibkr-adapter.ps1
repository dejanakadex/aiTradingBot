param(
    [string] $IbkrApiDll = 'C:\TWS API\source\CSharpClient\client\bin\Release\net8.0\CSharpAPI.dll'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dll = (Resolve-Path -LiteralPath $IbkrApiDll -ErrorAction Stop).Path
$outputDll = Join-Path $repoRoot 'TradingBot.Infrastructure\bin\Release\net10.0\CSharpAPI.dll'

Push-Location $repoRoot
try {
    $constants = dotnet msbuild TradingBot.Infrastructure/TradingBot.Infrastructure.csproj "-p:IbkrApiDll=$dll" -getProperty:DefineConstants
    if ($LASTEXITCODE -ne 0 -or $constants -notmatch 'IBKR_API_AVAILABLE') { throw 'The real IBKR adapter compilation symbol is missing.' }

    dotnet restore TradingBot.sln "-p:IbkrApiDll=$dll"
    if ($LASTEXITCODE -ne 0) { throw 'IBKR restore failed.' }

    dotnet build TradingBot.sln --configuration Release --no-restore "-p:IbkrApiDll=$dll"
    if ($LASTEXITCODE -ne 0) { throw 'IBKR Release build failed.' }

    if (-not (Test-Path -LiteralPath $outputDll)) { throw 'CSharpAPI.dll was not copied to the Infrastructure output.' }
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $outputDll -Algorithm SHA256).Hash) {
        throw 'The built CSharpAPI.dll differs from the selected official DLL.'
    }

    dotnet test TradingBot.sln --configuration Release --no-build --no-restore "-p:IbkrApiDll=$dll"
    if ($LASTEXITCODE -ne 0) { throw 'IBKR solution tests failed.' }

    Write-Host 'IBKR adapter build, DLL copy and solution tests passed.'
}
finally {
    Pop-Location
}
