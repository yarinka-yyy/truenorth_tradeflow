$ErrorActionPreference = 'Stop'

$softRoot = $PSScriptRoot
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $softRoot)
$artifactRoot = Join-Path $workspaceRoot 'artifacts\001-windows-shell'
$projectPath = Join-Path $softRoot 'TrueNorth.TradeFlow.csproj'
$setupScript = Join-Path $softRoot 'installer\setup.iss'
$version = ([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'The project Version property is missing.'
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
Push-Location $softRoot
try {
    dotnet publish .\TrueNorth.TradeFlow.csproj -c Release -r win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:USERPROFILE 'AppData\Local\Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:USERPROFILE 'AppData\Local\Programs\Inno Setup 7\ISCC.exe')
)
$compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$compilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compilerPath -and $compilerCommand) {
    $compilerPath = $compilerCommand.Source
}
if (-not $compilerPath) {
    throw 'No user-local Inno Setup compiler was found. Install one from jrsoftware.org, then rerun this script.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $compilerPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') {
    throw "The Inno Setup compiler signature or publisher is not verified: $compilerPath"
}

& $compilerPath "/DAppVersion=$version" $setupScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$setupPath = Join-Path $artifactRoot "TrueNorth-TradeFlow-Setup-$version-win-x64.exe"
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Expected installer was not created: $setupPath"
}

Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
