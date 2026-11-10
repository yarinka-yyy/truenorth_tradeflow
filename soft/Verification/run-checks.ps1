$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$packageRoot = Join-Path $workspaceRoot 'artifacts\001-windows-shell\runtime-recovery-10.0.14'
$packageSource = Join-Path $packageRoot 'source'
$packageCache = Join-Path $packageRoot 'cache'
$useOfflineCache = (Test-Path -LiteralPath $packageSource) -and (Test-Path -LiteralPath $packageCache)

$previousRestoreSources = $env:RestoreSources
$previousNugetPackages = $env:NUGET_PACKAGES
$previousNugetAudit = $env:NuGetAudit
Push-Location $PSScriptRoot
try {
    if ($useOfflineCache) {
        $env:RestoreSources = "$packageSource;https://api.nuget.org/v3/index.json"
        $env:NUGET_PACKAGES = $packageCache
        $env:NuGetAudit = 'false'
    }
    dotnet run --project .\TrueNorth.TradeFlow.Verification.csproj -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Settings verification failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
    $env:RestoreSources = $previousRestoreSources
    $env:NUGET_PACKAGES = $previousNugetPackages
    $env:NuGetAudit = $previousNugetAudit
}
