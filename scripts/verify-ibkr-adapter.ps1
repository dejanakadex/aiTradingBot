param(
    # CI has already restored, built and tested with coverage.
    [switch] $SkipBuildAndTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$vendorRoot = Join-Path $repoRoot 'third_party/ibkr/10.49.02'
$dll = Join-Path $vendorRoot 'CSharpAPI.dll'
$outputDll = Join-Path $repoRoot 'TradingBot.Infrastructure/bin/Release/net10.0/CSharpAPI.dll'
$publishRoot = Join-Path $repoRoot 'TestResults/ibkr-publish'

function Assert-SameFile([string] $Expected, [string] $Actual) {
    if (-not (Test-Path -LiteralPath $Actual -PathType Leaf)) { throw "Required output is missing: $Actual" }
    if ((Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash) {
        throw "Output differs from the repository dependency: $Actual"
    }
}

Push-Location $repoRoot
try {
    if (-not $SkipBuildAndTests) {
        dotnet restore TradingBot.sln
        if ($LASTEXITCODE -ne 0) { throw 'IBKR restore failed.' }

        dotnet build TradingBot.sln --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'IBKR Release build failed.' }

        dotnet test TradingBot.sln --configuration Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'IBKR solution tests failed.' }
    }

    Assert-SameFile $dll $outputDll
    dotnet publish TradingBot.Web/TradingBot.Web.csproj --configuration Release --no-build --no-restore --output $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'IBKR publish failed.' }
    Assert-SameFile $dll (Join-Path $publishRoot 'CSharpAPI.dll')
    foreach ($file in @('LICENSE.txt', 'README.md', 'CSharpAPI-source.zip')) {
        Assert-SameFile (Join-Path $vendorRoot $file) (Join-Path $publishRoot "third_party/ibkr/$file")
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'Google.Protobuf.dll'))) {
        throw 'The published API runtime dependency Google.Protobuf.dll is missing.'
    }
    $dependencies = Get-Content -LiteralPath (Join-Path $publishRoot 'TradingBot.Web.deps.json') -Raw | ConvertFrom-Json
    if (-not ($dependencies.libraries.PSObject.Properties.Name -match '^CSharpAPI/')) {
        throw 'Published runtime dependency manifest does not contain CSharpAPI.'
    }

    # Negative checks use disposable alternate inputs; never rename/remove the real DLL.
    $missing = Join-Path $repoRoot "TestResults/missing-ibkr-$([guid]::NewGuid()).dll"
    $failure = dotnet build TradingBot.Infrastructure/TradingBot.Infrastructure.csproj --configuration Release --no-restore "-p:IbkrApiDll=$missing" 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $failure -notmatch 'IBKR001') { throw 'Missing DLL must fail with IBKR001.' }
    $invalid = Join-Path $repoRoot "TestResults/invalid-ibkr-$([guid]::NewGuid()).dll"
    try {
        [System.IO.File]::WriteAllText($invalid, 'Invalid test artifact, not an API assembly.')
        $failure = dotnet build TradingBot.Infrastructure/TradingBot.Infrastructure.csproj --configuration Release --no-restore "-p:IbkrApiDll=$invalid" 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -or $failure -notmatch 'IBKR002') { throw 'Changed DLL must fail with IBKR002.' }
    }
    finally {
        if (Test-Path -LiteralPath $invalid) { Remove-Item -LiteralPath $invalid }
    }

    Write-Host 'Required IBKR adapter, publish contents and missing/changed DLL guards passed. No broker connection was opened.'
    $global:LASTEXITCODE = 0
}
finally {
    Pop-Location
}
